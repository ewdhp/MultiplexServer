using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Server;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Server
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .Build();
            var port = configuration["Server:Port"];
            var serviceProvider = new ServiceCollection()
                .AddLogging(loggingBuilder =>
                {
                    loggingBuilder.AddConfiguration(configuration.GetSection("Logging"));
                    loggingBuilder.AddConsole();
                    loggingBuilder.AddFile("logs/log.txt");
                })
                .AddSingleton<WSProxy>(
                provider =>
                {
                    var logger = provider.GetService<ILogger<WSProxy>>();
                    if (logger == null)
                        throw new InvalidOperationException
                        ("Required services are not registered.");
                    return new WSProxy
                    (logger, $"http://localhost:{port}/");
                })
                .BuildServiceProvider();

            var webSocketServer = serviceProvider.GetService<WSProxy>();
            if (webSocketServer != null)
                await webSocketServer.StartAsync();
        }
    }
}
/*
{
    "Timestamp": "2023-10-01T12:34:56Z",
    "Protocol": "WebSocket",
    "Success": true,
    "RequestId": "12345",
    "SessionId": "abcde",
    "ErrorCode": null,
    "ErrorMessage": null,
    "EndPoint": "ws://localhost:5000",
    "Data": ["example data 1", "example data 2"]
}
*/