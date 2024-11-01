using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Server
{
    public class WSProxy
    {
        private readonly
        ConcurrentDictionary<string, WebSocket> _connections = new
        ConcurrentDictionary<string, WebSocket>();

        private readonly ILogger<WSProxy> _logger;
        private readonly HttpListener _listener;
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly UdpClient _udpClient = new UdpClient();

        public WSProxy(ILogger<WSProxy> logger, IConfiguration configuration)
        {
            _logger = logger;
            _listener = new HttpListener();
            var port = configuration["WebSocketServer:Port"];
            _listener.Prefixes.Add($"http://localhost:{port}/");
        }
        public async Task StartAsync()
        {
            _listener.Start();
            _logger.LogInformation("Server started listening");

            while (true)
            {
                var context = await _listener.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                {
                    var websocketContext = await context.AcceptWebSocketAsync(null);
                    var websocket = websocketContext.WebSocket;
                    var streamId = Guid.NewGuid().ToString();

                    _logger.LogInformation(
                        "New WebSocket connection established for streamId {StreamId}",
                        streamId);

                    // Handle incoming messages in a separate task
                    _ = Task.Run(() => HandleWSConnection(websocket, streamId));
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }
        public async Task BroadcastAsync(Message message)
        {
            foreach (var connection in _connections)
            {
                var websocket = connection.Value;
                var streamId = connection.Key;

                try
                {
                    var buffer = message.Serialize();
                    await websocket.SendAsync(new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                    "Error broadcasting message to streamId {StreamId}",
                    streamId);
                }
            }
        }
        public async Task HandleWSConnection(WebSocket websocket, string streamId)
        {
            // Store the WebSocket connection
            _connections.TryAdd(streamId, websocket);

            var buffer = new ArraySegment<byte>(new byte[1024]);

            try
            {
                while (websocket.State == WebSocketState.Open)
                {
                    var result = await websocket.ReceiveAsync(buffer, CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        _logger.LogInformation("New request");
                        // Deserialize incoming message
                        var request = Message.Deserialize(buffer.Array ??
                        throw new ArgumentNullException(nameof(buffer.Array),
                        "Buffer array cannot be null."), result.Count);
                        _logger.LogInformation("Protocol: {Protocol}", request.Protocol);
                        // Dispatch request to corresponding backend service
                        var response = await DispatchAsync(request);
                        //log the response data

                        // Serialize response
                        var responseBuffer = response.Serialize();

                        // Send response back to client
                        await websocket.SendAsync(new ArraySegment<byte>(responseBuffer),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Close WebSocket connection
                        await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "",
                        CancellationToken.None);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                "Error handling WebSocket message for streamId {StreamId}",
                streamId);
                // Handle error (e.g., send error response to client)
                await websocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "", CancellationToken.None);
            }
            finally
            {
                // Remove connection when closed
                _connections.TryRemove(streamId, out _);
                _logger.LogInformation("WebSocket connection closed for streamId {StreamId}", streamId);
            }
        }
        public async Task<Message> DispatchAsync(Message request)
        {
            _logger.LogInformation("Dispatching request with Protocol: {Protocol}", request.Protocol);

            // Determine the protocol based on the request
            var protocol = request.Protocol;
            var tasks = new List<Task<Message>>();

            foreach (var endPoints in request.Data)
            {
                // Dispatch each request asynchronously and add the task to the list
                tasks.Add(DispatchRequestAsync(protocol!, endPoints));
            }

            // Await the completion of all dispatched tasks
            var responses = await Task.WhenAll(tasks);

            // Combine responses into a single WebSocketResponse
            var combinedResponse = new Message
            {
                Success = responses.All(r => r.Success),
                Data = responses.SelectMany(r => r.Data).ToList()
            };

            return combinedResponse;
        }
        private async Task<Message> DispatchRequestAsync(string protocol, string request)
        {
            try
            {
                return await DispatchWebSocketAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                "Error dispatching request with Protocol: {Protocol}",
                protocol);

                return new Message
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
        private async Task<Message> DispatchWebSocketAsync(string request)
        {
            _logger.LogInformation("Dispatching WebSocket request to {EndPoint}", request);

            try
            {
                var webSocket = new ClientWebSocket();
                if (string.IsNullOrEmpty(request))
                {
                    throw new ArgumentNullException(nameof(request),
                    "EndPoint cannot be null or empty.");
                }
                await webSocket.ConnectAsync(new Uri(request), CancellationToken.None);

                string jsonString = @"
                {
                ""Timestamp"": ""2023-10-01T12:34:56Z"",
                ""Protocol"": ""WebSocket"",
                ""RequestId"": ""12345"",
                ""SessionId"": ""abcde"",
                ""Data"": [
                    ""json string data"",
                ]
                }";
                var requestData = JsonSerializer.Serialize(jsonString);
                var buffer = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(requestData));

                await webSocket.SendAsync(
                    buffer, WebSocketMessageType.Text,
                    true, CancellationToken.None);

                var responseBuffer = new byte[1024];
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(responseBuffer),
                    CancellationToken.None);

                var responseData = System.Text.Encoding.UTF8.GetString(responseBuffer, 0, result.Count);

                var response = JsonSerializer.Deserialize<Message>(responseData) ??
                               throw new InvalidOperationException("Deserialized response is null.");
                _logger.LogInformation("Response to ws Data: {ResponseData}", response.Data);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dispatching WebSocket request");
                return new Message
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
    }
    public class Message
    {
        public string? Timestamp { get; set; }
        public string? Protocol { get; set; }
        public string? Async { get; set; }
        public bool Success { get; set; }
        public string? RequestId { get; set; }
        public string? SessionId { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? EndPoint { get; set; }
        public List<string> Data { get; set; } = new List<string>();
        private static readonly ILogger<Message> _logger;

        static Message()
        {
            // Initialize the logger
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.AddFile("logs/websocket_request.log");
            });
            _logger = loggerFactory.CreateLogger<Message>();
        }
        public static Message Deserialize(byte[] data, int count)
        {
            var jsonString = System.Text.Encoding.UTF8.GetString(data, 0, count);
            _logger.LogInformation("Deserializing JSON");

            try
            {
                return JsonSerializer.Deserialize<Message>(jsonString) ??
                       throw new InvalidOperationException("Deserialized response is null.");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error deserializing JSON: {JsonString}", jsonString);
                throw;
            }
        }
        public byte[] Serialize()
        {
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this);
        }
    }
    public static class BinaryExtensions
    {
        public static List<object> ReadListObject(this BinaryReader reader)
        {
            var count = reader.ReadInt32();
            var list = new List<object>();

            for (var i = 0; i < count; i++)
            {
                list.Add(reader.ReadString());
            }

            return list;
        }
        public static void WriteListObject(this BinaryWriter writer, List<object> value)
        {
            writer.Write(value.Count);

            foreach (var item in value)
            {
                writer.Write(item?.ToString() ?? string.Empty);
            }
        }
    }
}
