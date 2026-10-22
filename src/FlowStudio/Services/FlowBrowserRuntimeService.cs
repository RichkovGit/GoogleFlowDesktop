using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

public sealed record FlowBrowserHttpResponse(int StatusCode, string Body)
{
    public override string ToString() => $"FlowBrowserHttpResponse {{ StatusCode = {StatusCode}, Body = [redacted] }}";
}

public sealed class FlowBrowserRuntimeException(string message) : Exception(message);

/// <summary>
/// First-party Flow actions in an external Chrome/Edge process using only Flow Studio's
/// dedicated sign-in profile. The native application keeps its own UI. This service never
/// enters credentials, handles a challenge, changes browser detection, or retries a write.
/// </summary>
public sealed partial class FlowBrowserRuntimeService : IAsyncDisposable
{
    internal const int MaximumResponseBytes = 32 * 1024 * 1024;
    internal const int MaximumFormBytes = 24 * 1024 * 1024;
    internal const string BatchPath = "/_/AiSandboxAngularFrontend/data/batchexecute";
    internal const string AgentPath = "/_/AiSandboxAngularFrontend/data/google.internal.labs.aisandbox.proto.flow.agent.v1.FlowCreationAgentService/StreamChat";
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "IMAGE_GENERATION", "VIDEO_GENERATION", "CHAT_GENERATION", "AUDIO_GENERATION", "UPLOAD_IMAGE"
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private FileStream? _profileLease;
    private FlowRuntimeCdpConnection? _browser;
    private FlowRuntimeCdpConnection? _page;
    private Uri? _endpoint;
    private Uri? _pageEndpoint;
    private bool _disposed;
    public FlowBrowserRuntimeService(){}
    internal FlowBrowserRuntimeService(FlowRuntimeCdpConnection page){_page=page;page.EventReceived=ObserveNetworkEvent;}

    public string LoginProfileDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowStudio", "LoginProfile");

    public async Task<FlowBrowserSession> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await EnsureReadyCoreAsync(linked.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Obtains a fresh, single-action token from the public, unmodified Google SDK.</summary>
    public async Task<string> GetActionTokenAsync(string action, CancellationToken cancellationToken = default)
    {
        if (!AllowedActions.Contains(action)) throw new ArgumentException("Неизвестное действие Flow.", nameof(action));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            _ = await EnsureReadyCoreAsync(linked.Token).ConfigureAwait(false);
            var evaluation = await _page!.InvokeAsync("Runtime.evaluate", new
            {
                expression = BuildActionTokenExpression(action), returnByValue = true,
                awaitPromise = true, userGesture = false, silent = true
            }, linked.Token, TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            using var output = ReadEvaluationJson(evaluation);
            if (output.RootElement.TryGetProperty("error", out var error))
                throw new FlowBrowserRuntimeException(ActionError(error.GetString()));
            if (!output.RootElement.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String ||
                token.GetString() is not { Length: > 0 and <= 32768 } value || value.Any(char.IsControl))
                throw new FlowBrowserRuntimeException("Google не выдал подтверждение действия. Откройте Flow и проверьте вход.");
            return value;
        }
        catch (FlowRuntimeCdpException)
        {
            await ForgetPageAsync().ConfigureAwait(false);
            throw new FlowBrowserRuntimeException("Служебное окно Flow не смогло подтвердить действие. Проверьте вход Google.");
        }
        catch (WebSocketException)
        {
            await ForgetPageAsync().ConfigureAwait(false);
            throw new FlowBrowserRuntimeException("Соединение со служебным окном Flow прервалось. Повторите действие вручную.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Performs one same-origin POST. A cancelled or failed write is never retried.</summary>
    public async Task<FlowBrowserHttpResponse> SendAsync(Uri endpoint, string formBody, CancellationToken cancellationToken = default)
    {
        ValidateActionEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(formBody);
        if (Encoding.UTF8.GetByteCount(formBody) > MaximumFormBytes)
            throw new FlowBrowserRuntimeException("Запрос Flow слишком большой для служебного подключения.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        string? operationId = null;
        try
        {
            var session = await EnsureReadyCoreAsync(linked.Token).ConfigureAwait(false);
            (endpoint, formBody) = WithCurrentBootstrap(endpoint, formBody, session);
            operationId = Guid.NewGuid().ToString("N");
            var evaluation = await _page!.InvokeAsync("Runtime.evaluate", new
            {
                expression = BuildFetchExpression(endpoint, formBody, operationId),
                returnByValue = true, awaitPromise = true, userGesture = false, silent = true
            }, linked.Token, TimeSpan.FromMinutes(11)).ConfigureAwait(false);
            using var output = ReadEvaluationJson(evaluation);
            var root = output.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new FlowBrowserRuntimeException(FetchError(error.GetString()));
            if (!root.TryGetProperty("status", out var status) || !status.TryGetInt32(out var statusCode) || statusCode is < 100 or > 599 ||
                !root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String)
                throw new FlowBrowserRuntimeException("Служебное окно Flow вернуло некорректный ответ.");
            return new FlowBrowserHttpResponse(statusCode, body.GetString()!);
        }
        catch (OperationCanceledException)
        {
            if (operationId is not null) await AbortFetchAsync(operationId).ConfigureAwait(false);
            await ForgetPageAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is FlowRuntimeCdpException or WebSocketException or JsonException)
        {
            if (operationId is not null) await AbortFetchAsync(operationId).ConfigureAwait(false);
            await ForgetPageAsync().ConfigureAwait(false);
            throw new FlowBrowserRuntimeException("Ответ Flow потерян. Запрос не повторён: проверьте медиатеку перед новой генерацией.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Releases the profile before ordinary manual sign-in. The service can be used again afterwards.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CloseCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<FlowBrowserSession> EnsureReadyCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(50));
        try
        {
            if (_process is null || _process.HasExited)
            {
                await CloseCoreAsync().ConfigureAwait(false);
                if (!Directory.Exists(LoginProfileDirectory))
                    throw new FlowBrowserRuntimeException("Сначала выполните обычный вход Google в Flow Studio.");
                try
                {
                    _profileLease = new FileStream(Path.Combine(LoginProfileDirectory, "FlowStudio.login.lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    throw new FlowBrowserRuntimeException("Профиль Flow занят окном входа или другим Flow Studio. Завершите вход и повторите.");
                }
                var portFile = Path.Combine(LoginProfileDirectory, "DevToolsActivePort");
                File.Delete(portFile);
                var startedAt = DateTime.UtcNow;
                var start = FlowBrowserLoginService.BuildBrowserStartInfo(
                    FlowBrowserLoginService.FindBrowserExecutable(), LoginProfileDirectory, true);
                start.WindowStyle = ProcessWindowStyle.Hidden;
                // Chromium may ignore WindowStyle; start-minimized keeps its external window unobtrusive.
                start.ArgumentList.Insert(start.ArgumentList.Count - 1, "--start-minimized");
                _process = Process.Start(start) ?? throw new FlowBrowserRuntimeException("Не удалось запустить служебное окно Flow.");
                _endpoint = await FlowBrowserLoginService.WaitForEndpointAsync(portFile, startedAt, _process, startup.Token).ConfigureAwait(false);
                _browser = await FlowRuntimeCdpConnection.ConnectAsync(_endpoint, startup.Token).ConfigureAwait(false);
            }
            var wait = Stopwatch.StartNew();
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new FlowBrowserRuntimeException("Служебное окно Flow было закрыто.");
                if (_page is null)
                {
                    var targets = await _browser!.InvokeAsync("Target.getTargets", new { }, startup.Token).ConfigureAwait(false);
                    var id = FlowBrowserLoginService.FindFlowTarget(targets);
                    if (id is not null)
                    {
                        _pageEndpoint = new UriBuilder(_endpoint!) { Path = "/devtools/page/" + id }.Uri;
                        _page = await FlowRuntimeCdpConnection.ConnectAsync(_pageEndpoint, startup.Token).ConfigureAwait(false);
                        _page.EventReceived=ObserveNetworkEvent;
                        await _page.InvokeAsync("Network.enable",new{},startup.Token).ConfigureAwait(false);
                    }
                }
                if (_page is not null)
                {
                    var result = await _page.InvokeAsync("Runtime.evaluate", new
                    {
                        expression = FlowBrowserLoginService.ReadWizExpression,
                        returnByValue = true, awaitPromise = false, userGesture = false, silent = true
                    }, startup.Token).ConfigureAwait(false);
                    var wiz = FlowBrowserLoginService.ReadWiz(result);
                    if (wiz.TryGetValue("awbSEf", out var signedIn) && signedIn == "true" &&
                        wiz.TryGetValue("SNlM0e", out var csrf) && !string.IsNullOrWhiteSpace(csrf))
                    {
                        var cookies = await _page.InvokeAsync("Network.getCookies", new
                        { urls = new[] { "https://flow.google.com/", "https://aisandbox-pa.googleapis.com/" } }, startup.Token).ConfigureAwait(false);
                        var version = await _browser!.InvokeAsync("Browser.getVersion", new { }, startup.Token).ConfigureAwait(false);
                        var userAgent = version.TryGetProperty("userAgent", out var ua) && ua.ValueKind == JsonValueKind.String ? ua.GetString() ?? "" : "";
                        if (userAgent.Length > 1024 || userAgent.Any(char.IsControl)) throw new FlowRuntimeCdpException();
                        var session = new FlowBrowserSession(FlowBrowserLoginService.ReadCookies(cookies), wiz, userAgent, DateTimeOffset.UtcNow);
                        if (session.HasGoogleSession) return session;
                    }
                }
                if (wait.Elapsed > TimeSpan.FromSeconds(35))
                    throw new FlowBrowserRuntimeException("Сеанс Flow не готов. Выполните обычный вход Google и дождитесь загрузки Flow; скрытое окно не выполняет вход за вас.");
                await Task.Delay(500, startup.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            await CloseCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static void ValidateActionEndpoint(Uri endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || endpoint.Host != "flow.google.com" ||
            !endpoint.IsDefaultPort || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.OriginalString.Length > 16384 ||
            endpoint.AbsolutePath is not (BatchPath or AgentPath))
            throw new ArgumentException("Допускаются только штатные адреса действий Flow.", nameof(endpoint));
    }

    internal static (Uri Endpoint, string FormBody) WithCurrentBootstrap(Uri endpoint, string formBody, FlowBrowserSession session)
    {
        var query = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < query.Length; i++)
        {
            var split = query[i].IndexOf('=');
            var name = Uri.UnescapeDataString(split < 0 ? query[i] : query[i][..split]);
            var key = name switch { "f.sid" => "FdrFJe", "bl" => "cfb2h", _ => null };
            if (key is not null && session.Wiz.TryGetValue(key, out var value) && value is not null)
                query[i] = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        }
        if (session.Wiz.TryGetValue("SNlM0e", out var csrf) && csrf is not null)
        {
            var fields = formBody.Split('&');
            for (var i = 0; i < fields.Length; i++)
            {
                var split = fields[i].IndexOf('=');
                if (Uri.UnescapeDataString(split < 0 ? fields[i] : fields[i][..split]) == "at")
                    fields[i] = "at=" + Uri.EscapeDataString(csrf);
            }
            formBody = string.Join('&', fields);
        }
        return (new UriBuilder(endpoint) { Query = string.Join('&', query) }.Uri, formBody);
    }

    internal static string BuildActionTokenExpression(string action)
    {
        if (!AllowedActions.Contains(action)) throw new ArgumentException("Неизвестное действие Flow.", nameof(action));
        return "(async()=>{if(location.origin!=='https://flow.google.com'||window.WIZ_global_data?.awbSEf!==true)return JSON.stringify({error:'AUTH'});" +
            "const key=window.WIZ_global_data?.xZbWve;if(typeof key!=='string'||!key)return JSON.stringify({error:'SDK'});" +
            "const deadline=Date.now()+45000;while(!window.grecaptcha?.enterprise?.ready&&Date.now()<deadline)await new Promise(r=>setTimeout(r,200));" +
            "const sdk=window.grecaptcha?.enterprise;if(typeof sdk?.execute!=='function')return JSON.stringify({error:'SDK'});" +
            // The site's extension protection must stay intact. Do not discover or extract its private execute function.
            "if(Function.prototype.toString.call(sdk.execute).includes('extension_hijack_detected'))return JSON.stringify({error:'SITE_UI_REQUIRED'});" +
            "let timer;try{await new Promise((resolve,reject)=>{timer=setTimeout(()=>reject(new Error('TIMEOUT')),10000);sdk.ready(()=>{clearTimeout(timer);resolve()})});" +
            "const token=await Promise.race([sdk.execute(key,{action:" + JsonSerializer.Serialize(action) + "}),new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error('TIMEOUT')),45000)})]);" +
            "return JSON.stringify({token})}catch{return JSON.stringify({error:'CHALLENGE'})}finally{clearTimeout(timer)}})()";
    }

    internal static string BuildFetchExpression(Uri endpoint, string formBody, string operationId)
    {
        ValidateActionEndpoint(endpoint);
        if (operationId.Length != 32 || !operationId.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid operation identifier.", nameof(operationId));
        return "(async()=>{if(location.origin!=='https://flow.google.com'||window.WIZ_global_data?.awbSEf!==true)return JSON.stringify({error:'AUTH'});" +
            "const id=" + JsonSerializer.Serialize(operationId) + ",controller=new AbortController();" +
            "window.__flowStudioRequests??=new Map();window.__flowStudioRequests.set(id,controller);const timer=setTimeout(()=>controller.abort(),600000);" +
            "try{const response=await fetch(" + JsonSerializer.Serialize(endpoint.AbsoluteUri) + ",{method:'POST',credentials:'same-origin',redirect:'error'," +
            "headers:{'Content-Type':'application/x-www-form-urlencoded;charset=UTF-8','X-Same-Domain':'1'},body:" + JsonSerializer.Serialize(formBody) + ",signal:controller.signal});" +
            "if(!response.body)return JSON.stringify({status:response.status,body:''});const reader=response.body.getReader(),decoder=new TextDecoder();let text='',size=0;" +
            "for(;;){const part=await reader.read();if(part.done)break;size+=part.value.byteLength;if(size>" + MaximumResponseBytes + "){controller.abort();return JSON.stringify({error:'SIZE'})}text+=decoder.decode(part.value,{stream:true})}" +
            "text+=decoder.decode();return JSON.stringify({status:response.status,body:text})}catch(error){return JSON.stringify({error:controller.signal.aborted?'CANCELLED':'NETWORK'})}" +
            "finally{clearTimeout(timer);window.__flowStudioRequests?.delete(id)}})()";
    }

    private static JsonDocument ReadEvaluationJson(JsonElement evaluation)
    {
        if (evaluation.TryGetProperty("exceptionDetails", out _) || !evaluation.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("type", out var type) || type.GetString() != "string" || !result.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.String || value.GetString() is not { } json)
            throw new FlowRuntimeCdpException();
        return JsonDocument.Parse(json);
    }

    private static string ActionError(string? code) => code switch
    {
        "AUTH" => "Служебный сеанс Google завершён. Повторите обычный вход в аккаунт.",
        "SITE_UI_REQUIRED" => "Google требует отправку действия штатным интерфейсом Flow. Публичный SDK защищён от внешнего вызова; защита не отключалась.",
        "SDK" => "Официальный SDK Google не загрузился. Откройте Flow и проверьте доступность сервиса.",
        _ => "Google не подтвердил действие. Если требуется CAPTCHA, выполните проверку самостоятельно в обычном окне Flow."
    };

    private static string FetchError(string? code) => code switch
    {
        "AUTH" => "Служебный сеанс Google завершён. Повторите обычный вход в аккаунт.",
        "SIZE" => "Ответ Flow превысил допустимый размер. Запрос не повторён; проверьте медиатеку.",
        "CANCELLED" => "Ожидание ответа Flow остановлено. Уже принятая облаком генерация может завершиться; проверьте медиатеку.",
        _ => "Служебный запрос Flow не завершился. Запрос не повторён; проверьте медиатеку перед новой генерацией."
    };

    private async Task AbortFetchAsync(string operationId)
    {
        if (_pageEndpoint is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await using var connection = await FlowRuntimeCdpConnection.ConnectAsync(_pageEndpoint, deadline.Token).ConfigureAwait(false);
            await connection.InvokeAsync("Runtime.evaluate", new
            {
                expression = "if(location.origin==='https://flow.google.com')window.__flowStudioRequests?.get(" + JsonSerializer.Serialize(operationId) + ")?.abort()",
                returnByValue = true, awaitPromise = false, userGesture = false, silent = true
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or FlowRuntimeCdpException) { }
    }

    private async ValueTask ForgetPageAsync()
    {
        if (_page is not null) await _page.DisposeAsync().ConfigureAwait(false);
        _page = null;
        _pageEndpoint = null;
        preparedProject=null;compatibilityInstalled=false;responseInterceptionInstalled=false;activeAgentRequests.Clear();regionResponses.Clear();observedRpcIds.Clear();regionResponseChanges=0;
    }

    private async Task CloseCoreAsync()
    {
        await ForgetPageAsync().ConfigureAwait(false);
        try
        {
            if (_browser is not null)
            {
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _browser.InvokeAsync("Browser.close", new { }, closeDeadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException or FlowRuntimeCdpException or IOException) { }
            }
            if (_process is not null && !_process.HasExited)
            {
                using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await _process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    using var killDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _process.WaitForExitAsync(killDeadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or NotSupportedException) { }
        finally
        {
            if (_browser is not null) await _browser.DisposeAsync().ConfigureAwait(false);
            _browser = null;
            _process?.Dispose();
            _process = null;
            _endpoint = null;
            _profileLease?.Dispose();
            _profileLease = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CloseCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}

internal sealed class FlowRuntimeCdpException(string message="Служебное окно вернуло некорректный ответ.") : Exception(message);

/// <summary>One in-flight command per connection, event-aware framing, bounded memory, no payload logging.</summary>
internal sealed class FlowRuntimeCdpConnection : IAsyncDisposable
{
    internal const int MaximumMessageBytes = 40 * 1024 * 1024;
    private readonly WebSocket _socket;
    private int _nextId;
    internal Action<string,JsonElement>? EventReceived {get;set;}
    internal FlowRuntimeCdpConnection(WebSocket socket) => _socket = socket;

    public static async Task<FlowRuntimeCdpConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" || endpoint.Port is < 1024 or > 65535 ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            !(ValidPath(endpoint.AbsolutePath, "/devtools/browser/") || ValidPath(endpoint.AbsolutePath, "/devtools/page/")))
            throw new FlowBrowserRuntimeException("Недопустимый адрес служебного окна Flow.");
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            return new FlowRuntimeCdpConnection(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    private static bool ValidPath(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length > prefix.Length && value.Length <= prefix.Length + 128 && value[prefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public async Task<JsonElement> InvokeAsync(string method, object parameters, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(7));
        var id = Interlocked.Increment(ref _nextId);
        var message = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await _socket.SendAsync(message.AsMemory(), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
        var buffer = new byte[32 * 1024];
        while (true)
        {
            using var reply = new MemoryStream();
            ValueWebSocketReceiveResult part;
            do
            {
                part = await _socket.ReceiveAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (part.MessageType != WebSocketMessageType.Text || reply.Length + part.Count > MaximumMessageBytes)
                    throw new FlowRuntimeCdpException();
                reply.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            try
            {
                using var document = JsonDocument.Parse(reply.GetBuffer().AsMemory(0, checked((int)reply.Length)));
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var replyId) || !replyId.TryGetInt32(out var actualId) || actualId != id)
                {if(root.TryGetProperty("method",out var eventMethod)&&root.TryGetProperty("params",out var eventParameters))EventReceived?.Invoke(eventMethod.GetString()??"",eventParameters);continue;}
                if(root.TryGetProperty("error",out var protocolError))throw new FlowRuntimeCdpException("CDP "+method+": "+(protocolError.TryGetProperty("message",out var protocolMessage)?protocolMessage.GetString():"error"));
                if(!root.TryGetProperty("result",out var result))throw new FlowRuntimeCdpException("Нет результата CDP "+method);
                return result.Clone();
            }
            catch (JsonException) { throw new FlowRuntimeCdpException(); }
        }
    }

    public ValueTask DisposeAsync() { _socket.Dispose(); return ValueTask.CompletedTask; }
}
