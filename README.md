Prototype of an Event Streaming system based on Kafka Streams and WebSockets. Implemented with .NET 8.0 ASP.NET Core MinimalAPI with MySQL DB hosted in Docker with DockerCompose orchestration.

General architecture.
1. Console Client that opens WebSocket connection for receiving real-time updates and uses GatewayAPI for queries and sending updates.
2. GatewayAPI that serves as an interface between client and backend services.
3. "Microservices emulator" service which both uses Restful api, WebSocket server and parallel Threads to simulate work of several services.

All logic based on "streams" or a continuous flow of events in different topics in Kafka. 
Raw inputs streamed to raw stream. Consumers of that validate and accept or decline events. Accepted events sent to Valid stream.
Valid stream is used to concurrently update DB state and send real-time updates to clients via WebSocket connection established earlier.
Additional streams are proposed (disputed events and suspicious events as well as the DB updates stream which can be used for updating general scoreboards with compressed DB info in real-time).

Client use an offset number to prove that they are operating on the lastest information when sending updates. 
Incorrect offset may be rejected to avoid two clients sending the same update independently. There are situations when offset is not enought to determine duplicstes, 
but the logic for that is outside of the scope of this proof of concept.
