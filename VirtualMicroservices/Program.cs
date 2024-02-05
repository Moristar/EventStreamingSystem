using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using MySql.Data.MySqlClient;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseWebSockets();

// Partition by matches, topics by "sport + stream type". See root folder kafka.txt
var producerConfig = new ProducerConfig
{
    BootstrapServers = "kafka:9092",
};

#region Emulation of "Microservice-consumers of event streams" (i.e. consumer-webserver)

#region Service A - raw incident consumer - validates incidents and streams valid incidents further
var eventStreamConsumerConfig = new ConsumerConfig
{
    BootstrapServers = "kafka:9092",
    GroupId = "football-group",
    AutoOffsetReset = AutoOffsetReset.Earliest
};

// Raw incidents stream - anything clients sent us (assuming authentication checks on previous API step)
using var eventStreamConsumer = new ConsumerBuilder<string, string>(eventStreamConsumerConfig).Build();
eventStreamConsumer.Subscribe("football-raw");
var cancellationTokenRaw = new CancellationTokenSource();

// Accepted incidents stream - incidents that were allowed to update the global state
using var incidentProducer = new ProducerBuilder<string, string>(producerConfig).Build();
using var incidentsConsumer = new ConsumerBuilder<string, string>(eventStreamConsumerConfig).Build();
incidentsConsumer.Subscribe("football-incidents");
var cancellationTokenIncidents = new CancellationTokenSource();

/*
// Accepted incidents stream - incidents that were allowed to update the global state
using var disputedIncidentProducer = new ProducerBuilder<string, string>(producerConfig).Build();
using var disputedIncidentsConsumer = new ConsumerBuilder<string, string>(eventStreamConsumerConfig).Build();
disputedIncidentsConsumer.Subscribe("football-disputed-incidents");
var cancellationTokenDisputedIncidents = new CancellationTokenSource();

// Stream of incidents that look like an attack or cheat
using var suspiciousIncidentProducer = new ProducerBuilder<string, string>(producerConfig).Build();
using var suspiciousIncidentsConsumer = new ConsumerBuilder<string, string>(eventStreamConsumerConfig).Build();
suspiciousIncidentsConsumer.Subscribe("football-suspicious-incidents");
var cancellationTokenSuspiciousIncidents = new CancellationTokenSource();
*/

// TODO: depending on the amount of events, consider having load-balanced services with one service per topic's key (i.e. - one per match)
// Because this call is not awaited, execution of the current method continues before the call is completed
#pragma warning disable CS4014
Task.Run(async () => {
    try
    {
        while (true)
        {
            var consumeResult = eventStreamConsumer.Consume(cancellationTokenRaw.Token);
            
            var value = JsonSerializer.Deserialize<FootballIncidentEvent>(consumeResult.Message.Value);
            if(value != null)
            {
                if(value.serverOffset == value.clientOffset) // client produced result with the latest information at the time (client sends offset he thinks is the latest = local + 1)
                {
                    // Validate
                    var validIncident = new ValidFootballIncidentEvent(value.serverOffset, value.eventType, value.eventData, value.producerID, value.serverTime);
                    // Some cases might call for more sophisticated time logic (e.g. client time is wrong as clock, but it may be actual game time)

                    var validIncidentJson = JsonSerializer.Serialize(validIncident);
                    // Post to valid events stream that can update game state
                    var result = await incidentProducer.ProduceAsync("football-incidents", new Message<string, string> { Key = consumeResult.Message.Key/*matchID*/, Value = validIncidentJson });
                }
                else if(value.serverOffset > value.clientOffset) // Client raised an event but the server state was already updated by the time server got it
                {
                    Console.WriteLine($"Late offset. Server = {value.serverOffset}, client = {value.clientOffset}");
                    // a. Either optimistically reject write hopin no information was lost and let user retry in the worst case.
                    // b. or implement logic for automatic resolving (f.eks. compare event data to determine whether the event is the same or not and allow writes if events are different)
                    // c. or push the decision further by writing to special football-disputed-incidents stream consumers of which will for example notify an arbiter who has to make a call and manually update game state
                }
                else // Client version is higher than server's  
                {
                    Console.WriteLine($"High offset. Server = {value.serverOffset}, client = {value.clientOffset}");

                    // Bug, glitch, attack... Ignore read but produce event in football-suspicious-incidents for review
                }
            }
            
            Console.WriteLine($"Consumed message '{consumeResult.Message.Value}' at: '{consumeResult.TopicPartitionOffset}'.");
        }
    }
    catch (OperationCanceledException)
    {
        eventStreamConsumer.Close();
    }
});
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

// TODO: consume disputed stream to handle conflicts

// TODO: consume suspicious stream to handle potential attacks
#endregion

#region Service B - update DB state from valid streams for data querying optimisation
var dbUpdaterConsumerConfig = new ConsumerConfig
{
    BootstrapServers = "kafka:9092",
    GroupId = "football-db-group",
    AutoOffsetReset = AutoOffsetReset.Earliest,

};

// Accepted incidents stream - incidents that were allowed to update the global state
using var dbIncidentsConsumer = new ConsumerBuilder<string, string>(dbUpdaterConsumerConfig).Build();
dbIncidentsConsumer.Subscribe("football-incidents");
var cancellationTokenDBIncidents = new CancellationTokenSource();

var connectionString = "Server=mysql;Port=3306;Database=EventsStreamDB;User=root;Password=rootpassword;";
var connection = new MySqlConnection(connectionString);

#pragma warning disable CS4014
Task.Run(async () => {
    try
    {
        // Assuming match already exists and we only update score etc.
        var sql = "UPDATE footballmatches SET Score = @Score, Comments = @Comments WHERE MatchID = @MatchID;";

        while (true)
        {
            var consumeResult = dbIncidentsConsumer.Consume(cancellationTokenDBIncidents.Token);         

            var match = JsonSerializer.Deserialize<ValidFootballIncidentEvent>(consumeResult.Message.Value);
        
            var dbResult = await connection.ExecuteAsync(sql, // IRL, actual normalization or serialisation should happen here so that the event info is translated to a tabular query-optimised form
                                                         new { MatchID = consumeResult.Message.Key, Score = match?.eventData, Comments = "Wow_" + match?.offset });
            
            // TODO: consider implementing buffer (List<ValidFootballIncidentEvent>), turning stream into batch job if amount of updates is too high

            // TODO: consider also publishing the DB updates to a separate event stream for someone who need to have snapshot info (like a real-time global scoreboard of all parallel matches)

            Console.WriteLine($"Updated DB score for match {consumeResult.Message.Key}.");
        }
    }
    catch (OperationCanceledException)
    {
        eventStreamConsumer.Close();
    }
});
#endregion

#region Service C - updates clients directly from valid events to avoid waiting for DB update and enable real-time updates with web-sockets 
var clientUpdaterConsumerConfig = new ConsumerConfig
{
    BootstrapServers = "kafka:9092",
    GroupId = "football-client-group",
    AutoOffsetReset = AutoOffsetReset.Earliest
};

// Accepted incidents stream - incidents that were allowed to update the global state
using var clientIncidentsConsumer = new ConsumerBuilder<string, string>(clientUpdaterConsumerConfig).Build();
clientIncidentsConsumer.Subscribe("football-incidents");
var cancellationTokenClientIncidents = new CancellationTokenSource();

// WebSocket opening request comes from client through and API Gateway
app.MapGet("/ws", async (HttpContext context) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        string clientAddress = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}";
        Console.WriteLine($"Client connected {clientAddress}");
        
        WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
        
        // Store connected clients to update them on stream events
        WebSocketManager.ConnectedClients.AddOrUpdate(clientAddress, webSocket, (key,newSocket) => newSocket);

        var buffer = new byte[1024 * 4];
        WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        while (!result.CloseStatus.HasValue)
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);

        // Remove client upon disconnection
        WebSocketManager.ConnectedClients.TryRemove(clientAddress, out _);
    }
    else
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Expected a WebSocket request");
    }
});

// Listen to valid match updates and send them to all clients via websocket connections
#pragma warning disable CS4014
Task.Run(async () => {
    try
    {
        while (true)
        {
            var consumeResult = clientIncidentsConsumer.Consume(cancellationTokenClientIncidents.Token);         

            var update = JsonSerializer.Deserialize<ValidFootballIncidentEvent>(consumeResult.Message.Value);
            
            // Notify all the clients with the latest event data as well as the server offset 
            // TODO: For performance paralellize or split clients into groups with load-balanced services or both
            foreach (var client in WebSocketManager.ConnectedClients)
            {
                if (client.Value.State == WebSocketState.Open)
                {                 
                    var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(update)); // Updates client with both offset and the actual incremental change
                    await client.Value.SendAsync(new ArraySegment<byte>(message, 0, message.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            
            Console.WriteLine($"Delivered message to clients for match {consumeResult.Message.Key}.");
        }
    }
    catch (OperationCanceledException)
    {
        eventStreamConsumer.Close();
    }
});

#endregion
#endregion

var footballMatchesOffsets = new Dictionary<string, int>(); // Server offset (ours, not Kafka's) to handle simultaneous updates of same nature

#region Mock of rest api microservice behind api that produces original event streams
app.MapPost("/football/incidents", async ([FromBody] FootballIncident data) => {
    
    using var producer = new ProducerBuilder<string, string>(producerConfig).Build();
    try
    {
        int serverOffset = 0;
        lock(footballMatchesOffsets)
        {
            // Event stream of all incident requests - valid and invalid. 
            if(!footballMatchesOffsets.ContainsKey(data.matchID))
                footballMatchesOffsets[data.matchID] = 0;
           
            serverOffset = footballMatchesOffsets[data.matchID]++; // Assuming that this is the only place changes to the match occur, If there are other sources of match updates - need to implement stream joing.
        }

        var kafkaEvent = new FootballIncidentEvent(serverOffset, data.offset, data.eventType, data.eventData,data.producerID,data.producerTime,DateTime.Now.ToUniversalTime());
        var kafkaEventJson = JsonSerializer.Serialize(kafkaEvent);
        var result = await producer.ProduceAsync("football-raw", new Message<string, string> { Key = data.matchID, Value = kafkaEventJson });

        var msg = $"Delivered '{result.Value}' to '{result.TopicPartitionOffset}'";

        return Results.Ok($"{{ \"offset\": {serverOffset} }}");
    }
    catch (ProduceException<string, string> e)
    {
        Console.WriteLine($"Delivery failed: {e.Error.Reason}");
        return Results.BadRequest($"Errors occured during the execution. See logs.");
    }     
});

app.MapGet("/football/matches/{matchId}", (string matchId) => {
    int offset = 0;
    if(footballMatchesOffsets.ContainsKey(matchId))
        offset = footballMatchesOffsets[matchId];

    return new FootballMatch(matchId, "EURO 2024", "Dynamo-Shakhtar", "0:0", "", offset);
});

#endregion
app.Run();

// For Kafka
public record FootballIncident(int offset, string matchID, string eventType, string eventData, string producerID, DateTimeOffset producerTime);

public record FootballIncidentEvent(int serverOffset, int clientOffset, string eventType, string eventData, string producerID, DateTimeOffset producerTime, DateTimeOffset serverTime);

public record ValidFootballIncidentEvent(int offset, string eventType, string eventData, string producerID, DateTimeOffset time);


// For DB
public record FootballMatch(string MatchID, string Name, string Players, string Score, string Comments, int Offset);

// For WebSockets
public static class WebSocketManager
{
    public static ConcurrentDictionary<string, WebSocket> ConnectedClients = new ConcurrentDictionary<string, WebSocket>();
}