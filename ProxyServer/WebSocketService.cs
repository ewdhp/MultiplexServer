using EdjCase.ICP.Agent;
using EdjCase.ICP.Agent.Agents;
using EdjCase.ICP.Agent.Identities;
using EdjCase.ICP.Candid.Mapping;
using EdjCase.ICP.Candid.Models;
using EdjCase.ICP.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using ProxyServer.Cosmicrafts;
using ProxyServer.Cosmicrafts.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class WebSocketService : IHostedService
{
  private readonly ILogger<WebSocketService> _logger;
  private readonly IHostApplicationLifetime _appLifetime;
  private HttpListener? _listener;

  public static string testSeedPhrase = "wrong swear claim hold sunny prepare dove swamp clip home extend exercise";

  public WebSocketService(ILogger<WebSocketService> logger, IHostApplicationLifetime appLifetime)
  {
    _logger = logger;
    _appLifetime = appLifetime;
  }

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _listener = new HttpListener();
    _listener.Prefixes.Add("http://localhost:8080/");
    _listener.Start();
    _logger.LogInformation("WebSocket server started. Listening on http://localhost:8080/");

    _appLifetime.ApplicationStarted.Register(() =>
    {
      Task.Run(async () =>
          {
            while (!cancellationToken.IsCancellationRequested)
            {
              var context = await _listener.GetContextAsync();
              if (context.Request.IsWebSocketRequest)
              {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                await HandleWebSocketConnection(webSocketContext.WebSocket, cancellationToken);
              }
              else
              {
                context.Response.StatusCode = 400;
                context.Response.Close();
              }
            }
          }, cancellationToken);
    });

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    _listener?.Stop();
    return Task.CompletedTask;
  }

  public class AppMessage
  {
    [CandidName("text")]
    public string Text { get; set; }

    public AppMessage(string text)
    {
      this.Text = text;
    }
  }
  private async Task HandleWebSocketConnection(WebSocket webSocket, CancellationToken cancellationToken)
  {
    var buffer = new byte[1024 * 4];
    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

    while (!result.CloseStatus.HasValue)
    {
      var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
      _logger.LogInformation("server: recieved request, {Message}", message);

      _logger.LogInformation("Calling canister with identity...");
      string canisterResponse = await CallCanisterWithIdentity(message);

      _logger.LogInformation("Sending message to client");
      var responseMessage = new AppMessage(canisterResponse);
      var responseBuffer = Encoding.UTF8.GetBytes(responseMessage.Text);
      await webSocket.SendAsync(new ArraySegment<byte>(responseBuffer), WebSocketMessageType.Text, true, cancellationToken);
      _logger.LogInformation("Message sent to client");

      result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
    }

    await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, cancellationToken);
  }

  private async Task<string> CallCanisterWithIdentity(string request)
  {
    Principal canisterId = Principal.FromText("bkyz2-fmaaa-aaaaa-qaaaq-cai"); ;
    Uri gatewayUri = new Uri("wss://localhost:8081");

    try
    {
      _logger.LogInformation("Creating WebSocketBuilder...");
      var builder = new WebSocketBuilder<AppMessage>
      (canisterId, gatewayUri);

      _logger.LogInformation("WebSocketBuilder created.");


      _logger.LogInformation("Attempting to set the root key...");
      HttpAgent agent = new(httpBoundryNodeUrl: new Uri("http://localhost:4943"));
      SubjectPublicKeyInfo devRootKey = await agent.GetRootKeyAsync();
      builder = builder.WithRootKey(devRootKey);
      _logger.LogInformation("Root key set successfully.");


      _logger.LogInformation("Creating CosmicraftsApiClient...");
      var client = new CosmicraftsApiClient(agent,
      Principal.FromText("bkyz2-fmaaa-aaaaa-qaaaq-cai"));
      _logger.LogInformation("CosmicraftsApiClient created.");


      _logger.LogInformation("Calling canister function...");
      var (isSuccess, response) = await client.Signup("user1", 1);
      _logger.LogInformation("Canister function called successfully.");

      _logger.LogInformation(
        "Success: {isSuccess} Response: {response}", isSuccess, response
      );

      return response;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to get root key.");
      throw; // Consider handling this more gracefully
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
    string privateKeyBase64 = Convert.ToBase64String(privateKey.GetEncoded());
    string publicKeyBase64 = Convert.ToBase64String(publicKey.GetEncoded());
    _logger.LogInformation("[CandidApiManager] Identity saved to PlayerPrefs. PrivateKeyBase64: {PrivateKeyBase64}, PublicKeyBase64: {PublicKeyBase64}", privateKeyBase64, publicKeyBase64);
    return new Ed25519Identity(publicKey.GetEncoded(), privateKey.GetEncoded());
  }

}