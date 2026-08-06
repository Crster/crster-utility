using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services.Mcp
{
    /// <summary>Where a hosted CLI agent reaches the Cody tools, and the token that lets it in.</summary>
    internal sealed record CodyMcpEndpoint(string Url, string Token);

    /// <summary>
    /// Serves the Cody tools to hosted CLI agents over MCP, using the streamable HTTP transport on a
    /// loopback port. Only requests carrying the session token are answered, and the port is closed
    /// again as soon as no agent needs it.
    /// </summary>
    internal sealed class CodyMcpServer(ICodyMcpHost host) : IDisposable
    {
        private const string ProtocolVersion = "2025-06-18";

        private readonly CodyMcpTools _tools = new(host);
        private HttpListener? _listener;
        private CancellationTokenSource? _cancellation;
        private CodyMcpEndpoint? _endpoint;

        public CodyMcpEndpoint? Endpoint => _endpoint;

        public bool IsRunning => _listener?.IsListening == true;

        /// <summary>Starts the server if it is not running, and returns where to reach it.</summary>
        public CodyMcpEndpoint? Start()
        {
            if (_endpoint is not null && IsRunning) return _endpoint;

            Stop();
            try
            {
                var port = FindFreeLoopbackPort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
                listener.Start();

                _listener = listener;
                var cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                _endpoint = new CodyMcpEndpoint(
                    $"http://127.0.0.1:{port}/mcp",
                    Guid.NewGuid().ToString("N"));
                _ = AcceptLoopAsync(listener, cancellation);
                return _endpoint;
            }
            catch (Exception exception) when (exception is HttpListenerException
                or SocketException
                or ObjectDisposedException)
            {
                Stop();
                return null;
            }
        }

        public void Stop()
        {
            // Cancel but never dispose here: the accept loop still holds this token, and it disposes
            // the source itself once it has finished with it.
            try
            {
                _cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _cancellation = null;
            _endpoint = null;

            var listener = _listener;
            _listener = null;
            if (listener is null) return;
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => Stop();

        /// <summary>Asks the OS for an unused loopback port by binding to port zero and letting go.</summary>
        private static int FindFreeLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }

        // Nothing observes this task, so it must never let an exception escape: an unobserved
        // failure on a background thread would take the whole app down.
        private async Task AcceptLoopAsync(HttpListener listener, CancellationTokenSource cancellation)
        {
            var token = cancellation.Token;
            try
            {
                while (!token.IsCancellationRequested && listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync();
                    }
                    catch (Exception exception) when (exception is HttpListenerException
                        or ObjectDisposedException
                        or InvalidOperationException)
                    {
                        return;
                    }

                    // One agent can hold a call open while another sends the next one.
                    _ = HandleContextAsync(context, token);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                if (!IsAuthorized(context.Request))
                {
                    await WriteStatusAsync(context, 401);
                    return;
                }

                // The server never pushes on its own, so only POST carries traffic.
                if (string.Equals(context.Request.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(context, 204);
                    return;
                }

                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(context, 405);
                    return;
                }

                using var reader = new StreamReader(
                    context.Request.InputStream,
                    context.Request.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync(token);
                if (JsonNode.Parse(body) is not JsonObject message)
                {
                    await WriteJsonAsync(context, ErrorResponse(null, -32700, "The request was not a JSON object."));
                    return;
                }

                var response = await DispatchAsync(message, token);
                if (response is null)
                {
                    // A notification gets an acknowledgement with no body.
                    await WriteStatusAsync(context, 202);
                    return;
                }

                await WriteJsonAsync(context, response);
            }
            catch (Exception)
            {
                // Also unobserved. A failed tool call must never reach the runtime as an
                // unhandled exception, so the request is dropped and the server stays up.
                TryAbort(context);
            }
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            var expected = _endpoint?.Token;
            if (string.IsNullOrEmpty(expected)) return false;

            var header = request.Headers["Authorization"] ?? string.Empty;
            const string scheme = "Bearer ";
            if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;

            var presented = header[scheme.Length..].Trim();
            // Fixed-time compare, so a wrong token tells a caller nothing about the right one.
            return CryptographicEquals(presented, expected);
        }

        private static bool CryptographicEquals(string left, string right) =>
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(left),
                Encoding.UTF8.GetBytes(right));

        /// <summary>Returns the reply to send, or null when the message was a notification.</summary>
        private async Task<JsonObject?> DispatchAsync(JsonObject message, CancellationToken token)
        {
            var method = message["method"]?.GetValue<string>() ?? string.Empty;
            var id = message["id"]?.DeepClone();
            if (id is null) return null;

            var parameters = message["params"] as JsonObject ?? [];
            switch (method)
            {
                case "initialize":
                    return Result(id, new JsonObject
                    {
                        // Echo the version the client asked for when we know it, so a newer client
                        // does not have to fall back. The subset we serve is the same in all of them.
                        ["protocolVersion"] = NegotiateProtocolVersion(
                            parameters["protocolVersion"]?.GetValue<string>()),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "cody",
                            ["title"] = "Cody workspace",
                            ["version"] = "1.0.0"
                        },
                        ["instructions"] =
                            "Tools of the Crster Utility app hosting this session. Use cody_open_in_editor "
                            + "and cody_open_diff to put files in front of the user, cody_ask_user when you "
                            + "need a decision, and cody_run_workspace_command so build and test output stays "
                            + "visible in the app."
                    });

                case "ping":
                    return Result(id, []);

                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = CodyMcpTools.CreateDeclarations() });

                case "tools/call":
                    return Result(id, await CallToolAsync(parameters, token));

                default:
                    return ErrorResponse(id, -32601, $"This server does not handle {method}.");
            }
        }

        /// <summary>Protocol revisions whose tool calls and streamable HTTP framing we already match.</summary>
        private static readonly string[] SupportedProtocolVersions =
            ["2025-03-26", "2025-06-18", "2025-11-25"];

        private static string NegotiateProtocolVersion(string? requested) =>
            requested is not null && Array.IndexOf(SupportedProtocolVersions, requested) >= 0
                ? requested
                : ProtocolVersion;

        private async Task<JsonObject> CallToolAsync(JsonObject parameters, CancellationToken token)
        {
            var name = parameters["name"]?.GetValue<string>() ?? string.Empty;
            var arguments = parameters["arguments"] as JsonObject ?? [];
            var result = await _tools.ExecuteAsync(name, arguments, token);
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result.Output
                }),
                ["isError"] = !result.Success
            };
        }

        private static JsonObject Result(JsonNode id, JsonObject result) => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };

        private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        private static async Task WriteJsonAsync(HttpListenerContext context, JsonObject payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private static async Task WriteStatusAsync(HttpListenerContext context, int statusCode)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentLength64 = 0;
            await context.Response.OutputStream.FlushAsync();
            context.Response.Close();
        }

        private static void TryAbort(HttpListenerContext context)
        {
            try
            {
                context.Response.Abort();
            }
            catch (Exception)
            {
            }
        }
    }
}
