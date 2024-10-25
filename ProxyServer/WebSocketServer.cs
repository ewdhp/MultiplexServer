using System.Net;
using System.Net.WebSockets;
using System.Text;
using EdjCase.ICP.Agent;
using EdjCase.ICP.Agent.Agents;
using EdjCase.ICP.Agent.Identities;
using EdjCase.ICP.Candid.Models;
using EdjCase.ICP.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using ProxyServer.Cosmicrafts;
using static WebSocketService;

namespace ProxyServer
{
  public class WebSocketServer : IHostedService
  {
    private readonly ILogger<WebSocketServer> _logger;
    private readonly WebSocketServerSettings _settings;
    private HttpListener? _listener;

    public WebSocketServer(
      ILogger<WebSocketServer> logger,
      IOptions<WebSocketServerSettings> settings)
    {
      _logger = logger;
      _settings = settings.Value;

      if (_settings.Port <= 0 || _settings.Port > 65535)
      {
        throw new ArgumentException("Invalid WebSocket port configuration");
      }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
      _listener = new HttpListener();
      _listener.Prefixes.Add($"http://localhost:{_settings.Port}/");
      _logger.LogInformation("Starting proxy on port {Port}", _settings.Port);
      _listener.Start();

      Task.Run(async () =>
      {
        while (!cancellationToken.IsCancellationRequested)
        {
          var context = await _listener.GetContextAsync();
          if (context.Request.IsWebSocketRequest)
          {
            var webSocketContext = await
            context.AcceptWebSocketAsync(null);
            _ = HandleWebSocketConnection(
              webSocketContext.WebSocket,
                cancellationToken
                );
          }
          else
          {
            context.Response.StatusCode = 400;
            context.Response.Close();
          }
        }
      }, cancellationToken);

      return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
      _listener?.Stop();
      return Task.CompletedTask;
    }

    private async Task HandleWebSocketConnection(
      WebSocket webSocket,
        CancellationToken cancellationToken
      )
    {
      var buffer = new byte[1024 * 4];
      WebSocketReceiveResult result = await webSocket.ReceiveAsync(
        new ArraySegment<byte>(buffer),
        cancellationToken
        );

      while (!result.CloseStatus.HasValue)
      {
        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
        string canisterResponse = await CallCanisterWithIdentity(message);
        var responseBuffer = Encoding.UTF8.GetBytes(canisterResponse);
        await webSocket.SendAsync(
          new ArraySegment<byte>(responseBuffer),
          WebSocketMessageType.Text, true,
          cancellationToken);
        _logger.LogInformation(
          "Response sent to client"
          );

        result = await webSocket.ReceiveAsync(
          new ArraySegment<byte>(buffer),
          cancellationToken
          );
      }

      await webSocket.CloseAsync(
        result.CloseStatus.Value,
        result.CloseStatusDescription,
        cancellationToken);
    }

    private async Task<string> CallCanisterWithIdentity(string unityMsg)
    {
      String cmd = unityMsg; // "wou canister cosmicrafts Signup "
      _logger.LogInformation("Received command: {cmd}", cmd);
      try
      {
        _logger.LogInformation("Creating WebSocketBuilder...");
        Principal id = Principal.FromText("bkyz2-fmaaa-aaaaa-qaaaq-cai"); ;
        Uri uri = new Uri("wss://localhost:8080");
        var builder = new WebSocketBuilder<AppMessage>(id, uri);
        _logger.LogInformation("WebSocketBuilder created.");


        _logger.LogInformation("Creating agent and keys...");
        HttpAgent agent = new(httpBoundryNodeUrl: new Uri("http://localhost:4943"));
        SubjectPublicKeyInfo devRootKey = await agent.GetRootKeyAsync();
        builder = builder.WithRootKey(devRootKey);
        _logger.LogInformation("Root key set successfully.");


        _logger.LogInformation("Getting canister actor...");
        var cosmicrafts = new CosmicraftsApiClient(agent,
        Principal.FromText("bkyz2-fmaaa-aaaaa-qaaaq-cai"));
        _logger.LogInformation("CosmicraftsApiClient created.");


        _logger.LogInformation("Calling canister function...");
        var (isSuccess, response) = await cosmicrafts.Signup("user1", 1);
        _logger.LogInformation("Canister response: {response}", response);


        return response;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to call canister function");
        throw;
      }
    }
    private Ed25519Identity GenerateEd25519Identity()
    {
      byte[] seedBytes = Encoding.UTF8.GetBytes(testSeedPhrase);
      var sha256 = new Sha256Digest();
      byte[] hashOutput = new byte[sha256.GetDigestSize()];
      sha256.BlockUpdate(seedBytes, 0, seedBytes.Length);
      sha256.DoFinal(hashOutput, 0);
      var privateKey = new Ed25519PrivateKeyParameters(hashOutput, 0);
      var publicKey = privateKey.GeneratePublicKey();
      return new Ed25519Identity(
        publicKey.GetEncoded(),
        privateKey.GetEncoded()
      );
    }
  }

  public class WebSocketServerSettings
  {
    public int? Port { get; set; } = 8081; // Default value
  }
}