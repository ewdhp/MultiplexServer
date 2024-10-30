using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Server.Tests
{
    [TestClass]
    public class WSProxyTests
    {
        private const string ServerUrl = "http://localhost:5000/";
        private WSProxy _wsProxy;
        private Task _serverTask;

        [TestInitialize]
        public void Setup()
        {
            var serviceProvider = new ServiceCollection()
                .AddLogging(configure => configure.AddConsole())
                .BuildServiceProvider();

            var logger = serviceProvider.GetService<ILogger<WSProxy>>();
            _wsProxy = new WSProxy(logger, ServerUrl);
            _serverTask = _wsProxy.StartAsync();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _wsProxy.Stop();
            _serverTask.Wait();
        }

        [TestMethod]
        public async Task WebSocketServer_ShouldRespondToMultipleClientMessages()
        {
            using var clientWebSocket = new ClientWebSocket();
            await clientWebSocket.ConnectAsync(new Uri("ws://localhost:5000"), CancellationToken.None);

            var message = new Message
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Protocol = "HTTP",
                Success = true,
                RequestId = Guid.NewGuid().ToString(),
                SessionId = Guid.NewGuid().ToString(),
                EndPoint = "http://example.com",
                Data = new List<string> { "test data 1", "test data 2", "test data 3" }
            };

            var messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            await clientWebSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);

            var buffer = new byte[1024];
            var result = await clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            var responseString = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var response = JsonSerializer.Deserialize<Message>(responseString);

            Assert.IsNotNull(response);
            Assert.IsTrue(response.Success);
            Assert.AreEqual("HTTP", response.Protocol);
            Assert.AreEqual(3, response.Data.Count);
            CollectionAssert.AreEqual(new List<string> { "test data 1", "test data 2", "test data 3" }, response.Data);
        }
    }
}