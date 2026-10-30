using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Logging;
using PwA.Config;
using PwA.Game;

namespace PwA.Server;

/// <summary>
/// 本机 HTTP 服务：MCP（POST /mcp）给任意本地 agent 用，REST（/state /actions /act /events）给 World 用。
/// 只监听 127.0.0.1。
/// </summary>
internal sealed class BridgeServer
{
    private const string LogPrefix = "[PwA.Server]";
    private const int MaxBodyBytes = 1 << 20;

    private readonly ModSettings _settings;
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly SemaphoreSlim _actionGate = new(1, 1);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BridgeServer(ModSettings settings)
    {
        _settings = settings;
    }

    public static BridgeServer? Instance { get; private set; }

    public int Port { get; private set; }

    public string Role => PulseCache.Role;

    public DateTime StartedAt => _startedAt;

    public int ActionsRunning => _actionGate.CurrentCount == 0 ? 1 : 0;

    public static BridgeServer Start(ModSettings settings)
    {
        var server = new BridgeServer(settings);
        server.StartCore();
        Instance = server;
        return server;
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            if (_listener?.IsListening == true)
            {
                _listener.Stop();
            }
        }
        catch
        {
        }

        try
        {
            _listener?.Close();
        }
        catch
        {
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _cts?.Dispose();
        EndpointFile.Delete();
        Log.Info($"{LogPrefix} 已停止");
    }

    /// <summary>动作串行闸门：同一时刻只跑一个动作，避免两次点击撞在一起。</summary>
    public async Task<T> WithActionGateAsync<T>(Func<Task<T>> work)
    {
        if (!await _actionGate.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            throw ApiException.Conflict("busy", "另一个动作还在执行，稍后再试。", retryable: true);
        }

        try
        {
            return await work();
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private void StartCore()
    {
        var preferred = _settings.Port ?? DefaultPortForRole();
        var (listener, port) = Bind(preferred, _settings.AllowFallback);
        _listener = listener;
        Port = port;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ListenLoopAsync(listener, _cts.Token));
        if (_settings.PublishEndpoint)
        {
            EndpointFile.Write(port, Role, _startedAt);
        }

        Log.Info($"{LogPrefix} 监听 http://127.0.0.1:{port}/ （role={Role}，preferred={preferred}）");
    }

    /// <summary>客户端（第二个窗口）默认 8092，host 8091，单人 8090。避开 Coopanion 自己的 8080。</summary>
    private int DefaultPortForRole() => InstanceRole.Current switch
    {
        InstanceRole.Role.Client => 8092,
        InstanceRole.Role.Host => 8091,
        _ => 8090,
    };

    private static (HttpListener Listener, int Port) Bind(int preferred, bool allowFallback)
    {
        var candidates = new List<int> { preferred };
        if (allowFallback)
        {
            for (var offset = 1; offset < 20; offset++)
            {
                if (preferred + offset <= 65535)
                {
                    candidates.Add(preferred + offset);
                }
            }
        }

        Exception? last = null;
        foreach (var port in candidates)
        {
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                return (listener, port);
            }
            catch (Exception ex)
            {
                last = ex;
                try
                {
                    listener.Close();
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException(
            $"无法在 127.0.0.1:{preferred} 起服务（PWA_PORT 可改端口）。", last);
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(context), token);
            }
            catch (HttpListenerException) when (token.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"{LogPrefix} 监听循环异常: {ex}");
                if (context != null)
                {
                    await WriteJsonAsync(context.Response, 500, ErrorBody("listener_error", ex.Message));
                }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var request = context.Request;
        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        if (path.Length == 0)
        {
            path = "/";
        }

        var method = request.HttpMethod.ToUpperInvariant();
        var status = 200;
        try
        {
            var (statusCode, body) = await RouteAsync(method, path, request);
            status = statusCode;
            await WriteBufferAsync(context.Response, statusCode, body, "application/json; charset=utf-8");
        }
        catch (ApiException api)
        {
            status = api.Status;
            await WriteBufferAsync(context.Response, api.Status, api.ToJson(), "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            status = 500;
            Log.Error($"{LogPrefix} {method} {path} 失败: {ex}");
            await WriteBufferAsync(context.Response, 500, ErrorBody("internal_error", ex.Message), "application/json; charset=utf-8");
        }
        finally
        {
            Log.Info($"{LogPrefix} {method} {path} -> {status} ({stopwatch.ElapsedMilliseconds}ms)");
        }
    }

    private async Task<(int Status, JsonNode Body)> RouteAsync(string method, string path, HttpListenerRequest request)
    {
        switch (path)
        {
            case "/health":
                EnsureMethod(method, "GET");
                return (200, GameApi.Health(this));
            case "/state":
                EnsureMethod(method, "GET");
                return (200, await GameApi.GetStateAsync());
            case "/actions":
                EnsureMethod(method, "GET");
                return (200, await GameApi.GetActionsAsync());
            case "/events":
                EnsureMethod(method, "GET");
                return (200, EventRing.Since(ReadSince(request)));
            case "/act":
                EnsureMethod(method, "POST");
                return (200, await GameApi.ActAsync(await ReadJsonAsync(request)));
            case "/plan":
                EnsureMethod(method, "POST");
                return (200, await GameApi.PlanAsync(await ReadJsonAsync(request)));
            case "/mcp":
                if (method == "POST")
                {
                    return (200, await McpEndpoint.HandleAsync(await ReadJsonAsync(request)));
                }

                if (method == "GET")
                {
                    throw new ApiException(405, "method_not_allowed", "MCP 走 Streamable HTTP：请 POST JSON-RPC 到 /mcp。");
                }

                EnsureMethod(method, "POST");
                return (405, null!);
            case "/":
                return (200, new JsonObject
                {
                    ["service"] = "pwa",
                    ["mod_version"] = ModEntry.Version,
                    ["endpoints"] = new JsonArray("/mcp", "/health", "/state", "/actions", "/act", "/plan", "/events"),
                });
            default:
                throw new ApiException(404, "not_found", $"没有这个路径: {path}");
        }
    }

    private static void EnsureMethod(string method, string expected)
    {
        if (!string.Equals(method, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(405, "method_not_allowed", $"这个路径只接受 {expected}。");
        }
    }

    private static int ReadSince(HttpListenerRequest request)
    {
        var raw = request.QueryString["since"];
        return int.TryParse(raw, out var since) && since >= 0 ? since : 0;
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return new JsonObject();
        }

        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        if (text.Length > MaxBodyBytes)
        {
            throw ApiException.BadRequest("body_too_large", "请求体太大。");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest("invalid_json", $"请求体不是合法 JSON: {ex.Message}");
        }
    }

    private static JsonObject ErrorBody(string code, string message) => new()
    {
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["retryable"] = false,
            ["status_code"] = 500,
        },
    };

    private static Task WriteJsonAsync(HttpListenerResponse response, int status, JsonObject body) =>
        WriteBufferAsync(response, status, body, "application/json; charset=utf-8");

    private static async Task WriteBufferAsync(HttpListenerResponse response, int status, JsonNode body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body?.ToJsonString() ?? "{}");
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        response.Headers["Cache-Control"] = "no-store";
        try
        {
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            // 客户端提前断开（例如 World 超时重试），不是错误。
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
            }
        }
    }
}
