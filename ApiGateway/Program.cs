using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

Uri microserviceURI = new Uri("http://virtualmicroservices:8080");
HttpClient microservicesClient = new HttpClient() { BaseAddress = microserviceURI };

app.MapGet("/", () => "Hello World!");

app.MapGet("/football/matches/{matchId}", (string matchId) => microservicesClient.GetFromJsonAsync<FootballMatch>($"/football/matches/{matchId}"));

app.MapPost("/football/matches/{matchId}/incidents", (string matchId, [FromBody] FootballIncident incident) => microservicesClient.PostAsJsonAsync("/football/incidents", incident));

app.MapGet("/football/matches/{matchId}/wsevents", () => "Establish websockets retranslation or implement a proxy with YARP or outside of ASP.NET");


app.Run();


public record FootballIncident(int offset, string matchID, string eventType, string eventData, string producerID, DateTimeOffset producerTime);

public record FootballMatch(string MatchID, string Name, string Players, string Score, string Comments, int Offset);