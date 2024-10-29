using System.Net;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
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
using ProxyServer.Cosmicrafts.Models;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Cryptography;
using System.Security.Claims;

namespace ProxyServer
{
    public class WebSocketServer : IHostedService
    {
        private static readonly string SECRET_KEY = GenerateSecretKey(32);

        private readonly ILogger<WebSocketServer> _logger;
        private readonly WebSocketServerSettings _settings;
        private HttpListener? _listener;


        public WebSocketServer(
        ILogger<WebSocketServer> logger,
        IOptions<WebSocketServerSettings> settings)
        {
            _logger = logger;
            _settings = settings.Value;
            string SECRET_KEY = GenerateSecretKey(32);
            if (_settings.Port <= 0 || _settings.Port > 65535)
                throw new ArgumentException("Invalid WebSocket port");
        }


        public Task StartAsync(CancellationToken token)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_settings.Port}/");
            _logger.LogInformation("Starting proxy on port {Port}", _settings.Port);

            _listener.Start();

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        try
                        {
                            var wsContext = await context.AcceptWebSocketAsync(null);
                            if (wsContext == null)
                            {
                                _logger.LogError("Failed to accept WebSocket connection.");
                                context.Response.StatusCode = 500;
                                context.Response.Close();
                                return;
                            }

                            _ = HandleWSConn(wsContext.WebSocket, token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Exception occurred on ws connection.");
                            context.Response.StatusCode = 500;
                            context.Response.Close();
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
            },
            token
            );
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken _)
        {
            _listener?.Stop();
            return Task.CompletedTask;
        }


        private async Task HandleWSConn(WebSocket webSocket, CancellationToken token)
        {
            var buf = new byte[1024 * 4];
            WebSocketReceiveResult result = await
            webSocket.ReceiveAsync(new ArraySegment<byte>(buf), token);

            try
            {
                while (!result.CloseStatus.HasValue)
                {
                    _logger.LogInformation("Request received from client");

                    var response = Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(
                        await ProcessRequest(
                            Encoding.UTF8.GetString(
                            buf, 0, result.Count
                        ))
                    ));

                    await webSocket.SendAsync(
                        new ArraySegment<byte>(response),
                        WebSocketMessageType.Text, true, token
                    );

                    _logger.LogInformation("Response sent to client");

                    _ = await webSocket
                        .ReceiveAsync(
                        new ArraySegment<byte>(buf),
                        token
                    );
                }
            }
            finally
            {
                if (result.CloseStatus.HasValue)
                {
                    await webSocket.CloseAsync(
                        result.CloseStatus.Value,
                        result.CloseStatusDescription,
                        token
                    );
                }
            }
        }
        private async Task<object> ProcessRequest(string msg)
        {
            var request = DeserializeRequest(msg);
            if (request == null) return "Invalid request format";
            _logger.LogInformation("Received Request");

            string id =
            request.Auth?.UserId ??
            Guid.NewGuid().ToString();

            string sessionToken = JwtHelper.GenerateJwtToken(id);
            bool isNewSession = string.IsNullOrEmpty(sessionToken);

            if (!isNewSession)
                _logger.LogInformation("Existing Session Token");
            else
                _logger.LogInformation("New Session Token");

            if (string.IsNullOrWhiteSpace(sessionToken) ||
                sessionToken.Split('.').Length != 3)
                return "Invalid session token format";
            else
                if (!ValidateSessionToken(sessionToken))
                return "Invalid session token";

            var r = new WebSocketResponse
            {
                Id = request.Id,
                Type = $"{request.Metadata?.Type}_response",
                Status = "success",

                Auth = new WebSocketResponse.AuthDetails
                {
                    SessionId = sessionToken,
                    UserId = request.Auth?.UserId ?? "anonymous"
                },
                Metadata = new WebSocketResponse.MetadataDetails
                {
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    ResponseId = Guid.NewGuid().ToString()
                }
            };

            foreach (var call in request.Requests)
            {
                if (call.Backend != null)
                    r.Data[call.Backend] = await
                    RouteDispatcher(call, sessionToken);
                else
                    _logger.LogError(
                        "Backend identifier is null."
                    );
            }

            return EncryptMessage(
                SerializeResponse(r)
            );
        }


        private async Task<object> RouteDispatcher(
        WebSocketRequest.BackendCall call,
        string sessionToken)
        {
            var map = new Dictionary<string, object>();

            if (!await BKHS(sessionToken))
                return "Backend handshake failed.";

            foreach (var f in call.Functions)
            {
                if (call.Backend != null)
                {
                    object? bkResponse = await
                    Exec(call.Backend, f.Key, f.Value);
                    map[f.Key] = bkResponse ?? "null";

                    if (bkResponse is WebSocketResponse r)
                    {
                        _logger.LogInformation(
                            "Response from backend Type: {r.Type}"
                            , r.Type
                        );

                        map[f.Key] = bkResponse;
                    }
                    else
                        _logger.LogError(
                        "Response is null or invalid format.");
                }
                else
                {
                    _logger.LogError(
                        "Backend identifier is null."
                    );

                    map[f.Key] = "null";
                }

            }
            return map;
        }

        private async Task<object> Exec(
        string backend, string fn,
        WebSocketRequest.BackendCall
        .FunctionCall call)
        {
            var iClass = CreateBKn(backend);

            if (iClass == null)
            {
                _logger
                .LogError(
                $"Backend '{backend}' class not found.");
                return $"Backend '{backend}' not supported.";
            }

            var uri = Uri.IsWellFormedUriString(
                call.Url,
                UriKind.Absolute
            );

            if (uri && backend != "web3")
            {
                using var httpClient = new HttpClient();

                if (call.Url == null)
                {
                    _logger.LogError(
                    "Endpoint URL is null.");
                    return "Endpoint URL is null.";
                }

                var response = await httpClient.GetAsync(new Uri(call.Url));

                if (response.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<object>
                        (await response.Content.ReadAsStringAsync()) ?? "null";

                return "Failed to call the endpoint.";
            }
            else
            {
                MethodInfo? method = iClass.GetType().GetMethod(fn);

                if (method == null)
                {
                    _logger.LogError(
                    $"Method '{fn}' not found in class '{iClass.GetType().Name}'.");
                    return $"Method '{fn}' not found.";
                }

                ParameterInfo[] param = method.GetParameters();

                if (param.Length != call.Parameters.Count)
                {
                    _logger.LogError(
                    $"Parameter count mismatch for method '{fn}'. Expected {param.Length}, got {call.Parameters.Count}.");
                    return $"Parameter count mismatch for method '{fn}'.";
                }

                object[] convertedArgs = new
                object[call.Parameters.Count];
                for (int i = 0; i < call.Parameters.Count; i++)
                {
                    convertedArgs[i] = ConvertArgument(call
                    .Parameters[i], param[i].ParameterType);
                }

                object? r = method.Invoke(
                    iClass,
                    convertedArgs
                );

                if (r is Task t)
                {
                    await t.ConfigureAwait(false);
                    if (t.GetType().IsGenericType)
                    {
                        var resultProperty = t.GetType()
                            .GetProperty("Result");
                        if (resultProperty != null)
                            return resultProperty
                            .GetValue(t) ??
                            "null";
                    }
                }
                return r ?? "null";
            }
        }


        private WebSocketRequest? DeserializeRequest(string message)
        {
            try
            {
                var request = JsonSerializer
                .Deserialize<WebSocketRequest>(message);
                return request ?? new WebSocketRequest();
            }
            catch (JsonException ex)
            {
                _logger.LogError("Failed to deserialize reequest: {0}", ex.Message);
                throw new InvalidOperationException("Failed to deserialize request");
            }
        }
        private string SerializeResponse(WebSocketResponse response)
        {
            try
            {
                return JsonSerializer.Serialize(response);
            }
            catch (JsonException ex)
            {
                _logger.LogError(
                    "Failed to serialize response: {0}", ex.Message);
                throw new InvalidOperationException($"Failed to serialize response");
            }
        }
        private WebSocketResponse? DeserializeResponse(string message)
        {
            try
            {
                return JsonSerializer
                .Deserialize<WebSocketResponse>
                (message)!;
            }
            catch (JsonException ex)
            {
                _logger.LogError("Failed to deserialize response: {0}", ex.Message);
                throw new InvalidOperationException($"Failed to deserialize response");
            }
        }
        private static async Task<bool> BKHS(string sessionToken)
        {
            using var httpClient = new HttpClient();

            var reqMsg = new HttpRequestMessage(
                HttpMethod.Post,
                "https://url/handshake"
            );

            reqMsg.Headers.Add(
                "Authorization",
                $"Bearer {sessionToken}"
            );

            var r = await httpClient.SendAsync(reqMsg);
            return r.IsSuccessStatusCode;
        }
        private object CreateBKn(string backend)
        {
            var type = Type.GetType(
            $"ProxyServer.Backends.{backend}");

            if (type == null)
            {
                _logger.LogError($"Backend '{backend}' class not found.");
                throw new InvalidOperationException($"Backend '{backend}' class not found.");
            }

            var instance = Activator.CreateInstance(type);
            if (instance == null)
            {
                _logger.LogError($"Failed to create an instance of backend '{backend}'.");
                throw new InvalidOperationException($"Failed to create an instance of backend '{backend}'.");
            }
            return instance;
        }
        private static bool ValidateSessionToken(string token)
        {
            var principal =
            JwtHelper.ValidateJwtToken(token);
            if (principal != null)
                return true;
            else
                return false;
        }
        private static string EncryptMessage(string message)
        {
            return message;
        }
        private static readonly string[] WordList = new[]
        {
        "apple", "banana", "cherry", "date", "elderberry",
        "fig", "grape", "honeydew","kiwi", "lemon", "mango",
        "nectarine", "orange", "papaya", "quince", "raspberry",
        "strawberry", "tangerine", "ugli", "vanilla", "watermelon",
        "xigua", "yam", "zucchini"
        };
        public static string GenerateSeedPhrase()
        {
            Random random = new Random();
            StringBuilder seedPhrase = new StringBuilder();

            for (int i = 0; i < 12; i++)
            {
                if (i > 0) seedPhrase.Append(' ');
                seedPhrase.Append(WordList[random.Next(WordList.Length)]);
            }
            return seedPhrase.ToString();
        }
        private static Ed25519Identity RandomIdentityGen()
        {
            string phrase = GenerateSeedPhrase();
            byte[] seedBytes = Encoding.UTF8.GetBytes(phrase);
            var sha256 = new Sha256Digest();
            byte[] hashOutput = new byte[sha256.GetDigestSize()];
            sha256.BlockUpdate(seedBytes, 0, seedBytes.Length);
            sha256.DoFinal(hashOutput, 0);
            var privateKey = new Ed25519PrivateKeyParameters(hashOutput, 0);
            var publicKey = privateKey.GeneratePublicKey();
            return new Ed25519Identity(publicKey.GetEncoded(), privateKey.GetEncoded());
        }
        private static object ConvertArgument(string arg, Type targetType)
        {
            if (targetType == typeof(UnboundedUInt))
                return UnboundedUInt
                .FromBigInteger(
                    BigInteger.Parse(arg)
                );

            return Convert.ChangeType(arg, targetType);
        }
        private static string GenerateSecretKey(int length)
        {
            byte[] randomBytes = new byte[length];
            RandomNumberGenerator.Fill(randomBytes);
            return Convert.ToBase64String(randomBytes);
        }
        public class JwtHelper
        {
            public static string GenerateJwtToken(string userId)
            {
                var securityKey = new
                SymmetricSecurityKey(
                Convert.FromBase64String(SECRET_KEY));
                var credentials = new SigningCredentials(
                securityKey, SecurityAlgorithms.HmacSha256);
                var handler = new JwtSecurityTokenHandler();
                var decriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(new[]
                { new Claim("userId", userId) }),
                    Expires = DateTime.UtcNow.AddHours(1),
                    SigningCredentials = credentials
                };
                var token = handler.CreateToken(decriptor);
                return handler.WriteToken(token);
            }
            public static ClaimsPrincipal? ValidateJwtToken(string token)
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Convert.FromBase64String(SECRET_KEY);

                try
                {
                    var validationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new
                        SymmetricSecurityKey(key),
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ClockSkew = TimeSpan.Zero
                    };

                    var principal = tokenHandler.ValidateToken(
                        token, validationParameters,
                        out SecurityToken validatedToken
                        );

                    if (validatedToken is JwtSecurityToken jwtToken &&
                        jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256,
                        StringComparison.InvariantCultureIgnoreCase))
                        return principal;
                    else
                        throw new SecurityTokenException("Invalid token");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                    $"Token validation failed: {ex.Message}");
                    return null!;
                }
            }
        }

    }

    internal class AppMessage
    {
        public string? Message { get; set; }

    }
    public class WebSocketServerSettings
    {
        public int? Port { get; set; } = 8081;
    }
    public class WebSocketRequest
    {
        public string? Id { get; set; }               // Unique ID for the request

        public List<BackendCall> Requests
        { get; set; } = new List<BackendCall>();      // List of backend calls
        public AuthDetails? Auth { get; set; }        // Authentication details
        public MetadataDetails? Metadata { get; set; } // Metadata about the request

        public class BackendCall
        {
            public string? Backend { get; set; }
            // Backend identifier
            public Dictionary<string, FunctionCall> Functions { get; set; } = new
            Dictionary<string, FunctionCall>(); // Functions and their parameters

            public class FunctionCall
            {
                public string? Url { get; set; }     // Endpoint URL
                public List<string> Parameters
                { get; set; } = new List<string>(); // Function parameters
            }
        }

        public class AuthDetails
        {
            public string? SessionId { get; set; }    // Session ID
            public string? UserId { get; set; }       // User ID
        }

        public class MetadataDetails
        {
            public string? Timestamp { get; set; }    // Timestamp of the request
            public string? RequestId { get; set; } // Unique request ID
            public string? Type { get; set; }   // Web3, web2, stream 
        }
    }
    public class WebSocketResponse
    {
        public string? Id { get; set; }                     // Corresponding ID for the response
        public string? Type { get; set; }                   // Response type
        public string? Status { get; set; }                 // Status (success or error)
        public Dictionary<string, object> Data { get; set; }
        = new Dictionary<string, object>();                 // Response data
        public MetadataDetails? Metadata { get; set; }      // Metadata about the response
        public AuthDetails? Auth { get; set; }              // Authentication details

        public class MetadataDetails
        {
            public string? Timestamp { get; set; }          // Timestamp of the response
            public string? ResponseId { get; set; }         // Unique response ID
        }

        public class AuthDetails
        {
            public string? SessionId { get; set; }          // Session ID
            public string? UserId { get; set; }             // User ID
        }
    }

}


