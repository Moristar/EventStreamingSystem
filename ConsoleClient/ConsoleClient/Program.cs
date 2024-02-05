// See https://aka.ms/new-console-template for more information
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;


Console.WriteLine("Match client started");

int myOffset = -1;
const string matchId = "1";

#pragma warning disable CS4014
Task.Run(async () => 
{
    var webSocketsURI = new Uri("ws://localhost:8002/ws");

    using var webSocket = new ClientWebSocket();

    try
    {
        await webSocket.ConnectAsync(webSocketsURI, CancellationToken.None);
        Console.WriteLine("Connected to the server");

        byte[] buffer = new byte[1024 * 4];
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            else
            {
                var response = Encoding.UTF8.GetString(buffer, 0, result.Count);
                //Console.WriteLine($"Server says: {response}");
                var incident = JsonSerializer.Deserialize<ValidFootballIncidentEvent>(response);
                myOffset = (incident?.offset ?? 0) + 1;
                Console.WriteLine($"New score from WebSocket is: {incident?.eventData}, updated by {incident?.producerID}. New offset is {myOffset}");
            }
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"Exception: {e.Message}");
    }
});

int clientId = Random.Shared.Next();
var apiClient = new HttpClient() { BaseAddress = new Uri("http://localhost:8001") };

var match = await apiClient.GetFromJsonAsync<FootballMatch>($"/football/matches/{matchId}");
myOffset = match?.Offset ?? -1;
Console.WriteLine($"Current server offset for match {matchId} is {myOffset}");

while(true)
{
    Console.Write($"Enter new score (client {clientId}): ");
    var readText = Console.ReadLine();
    await apiClient.PostAsJsonAsync($"/football/matches/{matchId}/incidents", new FootballIncident(myOffset, matchId, "goal", readText ?? "", clientId.ToString(), DateTime.Now));
}


public record ValidFootballIncidentEvent(int offset, string eventType, string eventData, string producerID, DateTimeOffset time);
public record FootballIncident(int offset, string matchID, string eventType, string eventData, string producerID, DateTimeOffset producerTime);
public record FootballMatch(string MatchID, string Name, string Players, string Score, string Comments, int Offset);