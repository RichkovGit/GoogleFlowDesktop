using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

/// <summary>A cookie from Flow Studio's dedicated sign-in profile, never from a user's normal browser profile.</summary>
public sealed record FlowSessionCookie(
    string Name, string Value, string Domain, string Path, double? Expires,
    bool Secure, bool HttpOnly, string? SameSite)
{
    public override string ToString() => $"FlowSessionCookie {{ Name = {Name}, Value = [redacted] }}";
}

/// <summary>Credentials passed directly to the native Flow adapter. Do not log or display this object.</summary>
public sealed record FlowBrowserSession(
    IReadOnlyList<FlowSessionCookie> Cookies,
    IReadOnlyDictionary<string, string?> Wiz,
    string UserAgent,
    DateTimeOffset CapturedAt)
{
    public bool HasGoogleSession =>
        Wiz.TryGetValue("awbSEf", out var signedIn) && signedIn == "true" &&
        Cookies.Any(cookie => FlowBrowserLoginService.IsSessionCookie(cookie.Name) && !string.IsNullOrEmpty(cookie.Value));

    public override string ToString() => "FlowBrowserSession { Credentials = [redacted] }";
}

public sealed class FlowBrowserLoginException(string message) : Exception(message);

/// <summary>
/// Opens an isolated, ordinary Chrome/Edge window for manual Google sign-in only.
/// No automation or DevTools endpoint is present while the user authenticates.
/// After explicit user confirmation, closes that browser gracefully, then reopens
/// the same app-owned profile only to transfer the existing session to native HTTP.
/// No sign-in form, CAPTCHA, generation, or other site action is automated here.
/// </summary>
public sealed class FlowBrowserLoginService
{
    private static readonly SemaphoreSlim LoginGate = new(1, 1);
    private static readonly string[] WizKeys = ["awbSEf", "SNlM0e", "S06Grb", "cfb2h", "FdrFJe", "xZbWve"];
    private static readonly HashSet<string> AuthenticationCookieNames = new(StringComparer.Ordinal)
    {
        "SAPISID", "APISID", "SID", "HSID", "SSID", "SIDCC", "OSID", "__Secure-OSID",
        "__Secure-1PAPISID", "__Secure-3PAPISID", "__Secure-1PSID", "__Secure-3PSID",
        "__Secure-1PSIDTS", "__Secure-3PSIDTS", "__Secure-1PSIDCC", "__Secure-3PSIDCC"
    };
    // This expression only reads the named bootstrap fields. It never invokes a site function.
    internal const string ReadWizExpression = "JSON.stringify((()=>{if(location.origin!=='https://flow.google.com')return {awbSEf:false};const d=window.WIZ_global_data;" +
        "if(!d||d.awbSEf!==true)return {awbSEf:false};" +
        "return {awbSEf:d.awbSEf,SNlM0e:d.SNlM0e,S06Grb:d.S06Grb,cfb2h:d.cfb2h,FdrFJe:d.FdrFJe,xZbWve:d.xZbWve};})())";
    private volatile Process? _browserProcess;
    private TaskCompletionSource<bool>? _manualConfirmation;

    public string LoginProfileDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowStudio", "LoginProfile");

    public bool AwaitingManualConfirmation => Volatile.Read(ref _manualConfirmation) is not null;

    /// <summary>Restores an existing app-owned profile session. No login form is operated and no generation runs in this browser.</summary>
    public async Task<FlowBrowserSession> RestoreAsync(IProgress<string>? progress=null,CancellationToken cancellationToken=default)
    {
        if(!Directory.Exists(LoginProfileDirectory))throw new FlowBrowserLoginException("Сначала выполните вход Google.");
        if(!await LoginGate.WaitAsync(0,cancellationToken))throw new FlowBrowserLoginException("Вход Google уже открыт.");
        Process? process=null;CdpConnection? connection=null;FileStream? lease=null;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            lease=new FileStream(System.IO.Path.Combine(LoginProfileDirectory,"FlowStudio.login.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var portFile=System.IO.Path.Combine(LoginProfileDirectory,"DevToolsActivePort");File.Delete(portFile);
            var started=DateTime.UtcNow;
            progress?.Report("Восстанавливаем ранее выполненный вход Google…");
            process=Process.Start(BuildBrowserStartInfo(FindBrowserExecutable(),LoginProfileDirectory,true))??throw new FlowBrowserLoginException("Не удалось восстановить вход Google.");
            _browserProcess=process;
            var endpoint=await WaitForEndpointAsync(portFile,started,process,timeout.Token);
            connection=await CdpConnection.ConnectAsync(endpoint,timeout.Token);
            return await WaitForFlowSessionAsync(connection,endpoint,process,progress,timeout.Token);
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        {throw new FlowBrowserLoginException("Автовход не завершён. Выполните обычный вход Google в настройках аккаунта.");}
        finally
        {
            if(process!=null)await CloseOwnedBrowserAsync(process,connection);
            if(connection!=null)await connection.DisposeAsync();
            process?.Dispose();_browserProcess=null;lease?.Dispose();LoginGate.Release();
        }
    }

    /// <summary>Called only by the user's explicit “I have signed in” action in the native UI.</summary>
    public void ConfirmManualSignIn() => Volatile.Read(ref _manualConfirmation)?.TrySetResult(true);

    public bool IsBrowserRunning
    {
        get
        {
            try { return _browserProcess is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public async Task<FlowBrowserSession> LoginAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await LoginGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new FlowBrowserLoginException("Вход Google уже открыт. Завершите или отмените текущий вход.");

        Process? browserProcess = null;
        CdpConnection? browserConnection = null;
        FileStream? profileLease = null;
        FlowBrowserSession? capturedSession = null;
        var closed = true;
        var captureStage = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            Directory.CreateDirectory(LoginProfileDirectory);
            // The lock covers other Flow Studio processes as well as this service instance.
            try
            {
                profileLease = new FileStream(System.IO.Path.Combine(LoginProfileDirectory, "FlowStudio.login.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                throw new FlowBrowserLoginException("Вход Google уже открыт в другом окне Flow Studio.");
            }

            var activePortFile = System.IO.Path.Combine(LoginProfileDirectory, "DevToolsActivePort");
            // Delete only the known stale discovery file in the application-owned profile.
            // Browser profile contents and credentials are never deleted by sign-in.
            File.Delete(activePortFile);
            var browserExecutable = FindBrowserExecutable();
            progress?.Report("Открываем отдельное окно входа Google…");
            browserProcess = Process.Start(BuildBrowserStartInfo(browserExecutable, LoginProfileDirectory, captureSession: false))
                ?? throw new FlowBrowserLoginException("Не удалось открыть окно входа Google.");
            _browserProcess = browserProcess;
            closed = false;
            var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _manualConfirmation, confirmation);
            progress?.Report("Войдите в Google в обычном окне браузера и откройте Flow. Затем нажмите в приложении «Я вошёл — продолжить».");
            await confirmation.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _manualConfirmation, null);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);

            // A clean process exit lets Chrome flush its own encrypted session data.
            // Never kill the normal sign-in process to accelerate this handoff.
            progress?.Report("Сохраняем вход Google: закрываем только отдельное окно Flow Studio…");
            if (!await CloseManualBrowserAsync(browserProcess).ConfigureAwait(false))
                throw new FlowBrowserLoginException("Не удалось штатно закрыть окно входа. Закройте все окна входа Flow Studio и повторите подключение.");
            browserProcess.Dispose();
            browserProcess = null;
            _browserProcess = null;
            closed = true;
            cancellationToken.ThrowIfCancellationRequested();

            File.Delete(activePortFile);
            var startedAt = DateTime.UtcNow;
            captureStage = true;
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            progress?.Report("Переносим уже выполненный вход в нативное приложение. Повторный ввод пароля не требуется…");
            browserProcess = Process.Start(BuildBrowserStartInfo(browserExecutable, LoginProfileDirectory, captureSession: true))
                ?? throw new FlowBrowserLoginException("Не удалось получить сохранённый сеанс Google.");
            _browserProcess = browserProcess;
            closed = false;
            var endpoint = await WaitForEndpointAsync(activePortFile, startedAt, browserProcess, deadline.Token).ConfigureAwait(false);
            browserConnection = await CdpConnection.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            capturedSession = await WaitForFlowSessionAsync(browserConnection, endpoint, browserProcess, progress, deadline.Token).ConfigureAwait(false);
            progress?.Report("Сеанс Google получен. Закрываем окно входа…");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FlowBrowserLoginException(deadline.IsCancellationRequested
                ? captureStage
                    ? "Не удалось перенести готовый сеанс Flow за 45 секунд. Повторите обычный вход Google и убедитесь, что Flow открыт."
                    : "Время входа истекло. Повторите вход Google."
                : "Окно входа не ответило. Повторите вход Google.");
        }
        catch (WebSocketException)
        {
            throw new FlowBrowserLoginException("Связь с окном входа прервалась. Повторите вход Google.");
        }
        catch (Win32Exception)
        {
            throw new FlowBrowserLoginException("Windows не удалось запустить браузер для входа Google.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new FlowBrowserLoginException("Нет доступа к отдельному профилю входа Flow Studio.");
        }
        catch (IOException)
        {
            throw new FlowBrowserLoginException("Не удалось открыть отдельный профиль входа. Закройте прежнее окно входа Flow Studio и повторите.");
        }
        catch (CdpCommandException)
        {
            throw new FlowBrowserLoginException("Окно входа вернуло неожиданный ответ. Повторите вход Google.");
        }
        catch (JsonException)
        {
            throw new FlowBrowserLoginException("Не удалось прочитать ответ окна входа. Повторите вход Google.");
        }
        finally
        {
            Interlocked.Exchange(ref _manualConfirmation, null);
            if (browserProcess is not null)
                closed = captureStage
                    ? await CloseOwnedBrowserAsync(browserProcess, browserConnection).ConfigureAwait(false)
                    : await CloseManualBrowserAsync(browserProcess).ConfigureAwait(false);
            if (browserConnection is not null) await browserConnection.DisposeAsync().ConfigureAwait(false);
            _browserProcess = null;
            browserProcess?.Dispose();
            profileLease?.Dispose();
            LoginGate.Release();
        }

        if (!closed)
            throw new FlowBrowserLoginException("Не удалось закрыть окно входа. Закройте его и повторите подключение аккаунта.");
        return capturedSession ?? throw new FlowBrowserLoginException("Google не вернул сеанс Flow. Повторите вход.");
    }

    internal static bool IsSessionCookie(string name) => name is "SAPISID" or "__Secure-1PAPISID" or "__Secure-3PAPISID";

    internal static ProcessStartInfo BuildBrowserStartInfo(string executable, string profileDirectory, bool captureSession)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = profileDirectory
        };
        start.ArgumentList.Add("--user-data-dir=" + profileDirectory);
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--disable-background-mode");
        if (captureSession)
        {
            start.ArgumentList.Add("--remote-debugging-port=0");
            start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        }
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add("https://flow.google.com/");
        return start;
    }

    internal static string FindBrowserExecutable()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] paths =
        [
            System.IO.Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            System.IO.Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            System.IO.Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            System.IO.Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            System.IO.Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")
        ];
        return paths.FirstOrDefault(File.Exists) ?? throw new FlowBrowserLoginException(
            "Для входа Google установите Google Chrome или Microsoft Edge. Он используется только во время авторизации.");
    }

    internal static async Task<Uri> WaitForEndpointAsync(
        string activePortFile, DateTime startedAt, Process browserProcess, CancellationToken cancellationToken)
    {
        var waitUntil = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < waitUntil)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureBrowserOpen(browserProcess);
            try
            {
                if (File.Exists(activePortFile) && File.GetLastWriteTimeUtc(activePortFile) >= startedAt.AddSeconds(-2))
                {
                    var file = new FileInfo(activePortFile);
                    if (file.Length is > 0 and <= 4096)
                    {
                        var lines = await File.ReadAllLinesAsync(activePortFile, cancellationToken).ConfigureAwait(false);
                        if (lines.Length >= 2 && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
                            port is >= 1024 and <= 65535 && IsDevToolsPath(lines[1], "/devtools/browser/"))
                            return new Uri($"ws://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{lines[1]}");
                    }
                }
            }
            catch (IOException) { /* Chrome writes the small discovery file atomically or in two writes. */ }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        throw new FlowBrowserLoginException("Окно входа не запустилось. Закройте прежнее окно входа Flow Studio и повторите.");
    }

    private static async Task<FlowBrowserSession> WaitForFlowSessionAsync(
        CdpConnection browser, Uri endpoint, Process browserProcess,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        CdpConnection? page = null;
        string? pageId = null;
        var waitingForFlowReported = false;
        var started = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureBrowserOpen(browserProcess);
                if (started.Elapsed > TimeSpan.FromSeconds(30))
                    throw new FlowBrowserLoginException("Готовый сеанс Flow не найден. Повторите обычный вход Google; нажимайте «Я вошёл» после загрузки самого Flow.");
                var targetsResult = await browser.InvokeAsync("Target.getTargets", new { }, cancellationToken).ConfigureAwait(false);
                var target = FindFlowTarget(targetsResult);
                if (target is null)
                {
                    if (page is not null) await page.DisposeAsync().ConfigureAwait(false);
                    page = null;
                    pageId = null;
                    if (!waitingForFlowReported)
                    {
                        progress?.Report("Ожидаем загрузки Flow с уже сохранённым входом. В этом окне повторный вход не выполняется.");
                        waitingForFlowReported = true;
                    }
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (page is null || pageId != target)
                {
                    if (page is not null) await page.DisposeAsync().ConfigureAwait(false);
                    page = await CdpConnection.ConnectAsync(PageEndpoint(endpoint, target), cancellationToken).ConfigureAwait(false);
                    pageId = target;
                }
                try
                {
                    var evaluation = await page.InvokeAsync("Runtime.evaluate", new
                    {
                        expression = ReadWizExpression,
                        returnByValue = true,
                        awaitPromise = false,
                        userGesture = false,
                        silent = true
                    }, cancellationToken).ConfigureAwait(false);
                    var wiz = ReadWiz(evaluation);
                    if (wiz.TryGetValue("awbSEf", out var signedIn) && signedIn == "true" &&
                        wiz.TryGetValue("SNlM0e", out var csrf) && !string.IsNullOrWhiteSpace(csrf))
                    {
                        var cookieResult = await page.InvokeAsync("Network.getCookies", new
                        {
                            urls = new[] { "https://flow.google.com/", "https://aisandbox-pa.googleapis.com/" }
                        }, cancellationToken).ConfigureAwait(false);
                        var cookies = ReadCookies(cookieResult);
                        if (cookies.Any(cookie => IsSessionCookie(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Value)))
                        {
                            var version = await browser.InvokeAsync("Browser.getVersion", new { }, cancellationToken).ConfigureAwait(false);
                            var userAgent = ReadString(version, "userAgent") ?? "";
                            if (userAgent.Length > 1024 || userAgent.Any(char.IsControl))
                                throw new FlowBrowserLoginException("Окно входа вернуло некорректные параметры сеанса.");
                            return new FlowBrowserSession(cookies, wiz, userAgent, DateTimeOffset.UtcNow);
                        }
                    }
                }
                catch (CdpCommandException)
                {
                    // Navigation may replace a renderer while Google finishes signing in.
                    await page.DisposeAsync().ConfigureAwait(false);
                    page = null;
                    pageId = null;
                }
                catch (WebSocketException)
                {
                    await page.DisposeAsync().ConfigureAwait(false);
                    page = null;
                    pageId = null;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await page.DisposeAsync().ConfigureAwait(false);
                    page = null;
                    pageId = null;
                }
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (page is not null) await page.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static string? FindFlowTarget(JsonElement result)
    {
        if (!result.TryGetProperty("targetInfos", out var targets) || targets.ValueKind != JsonValueKind.Array) return null;
        foreach (var target in targets.EnumerateArray())
        {
            if (ReadString(target, "type") != "page" ||
                !Uri.TryCreate(ReadString(target, "url"), UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || url.Host != "flow.google.com" || !url.IsDefaultPort || url.UserInfo.Length != 0) continue;
            var id = ReadString(target, "targetId");
            if (id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return id;
        }
        return null;
    }

    internal static IReadOnlyDictionary<string, string?> ReadWiz(JsonElement evaluation)
    {
        var output = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (evaluation.TryGetProperty("exceptionDetails", out _) ||
            !evaluation.TryGetProperty("result", out var result) || ReadString(result, "type") != "string") return output;
        var text = ReadString(result, "value");
        if (text is null || text.Length > 32768) return output;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return output;
            foreach (var key in WizKeys)
            {
                if (!document.RootElement.TryGetProperty(key, out var value)) continue;
                output[key] = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                };
            }
        }
        catch (JsonException) { }
        return new ReadOnlyDictionary<string, string?>(output);
    }

    internal static IReadOnlyList<FlowSessionCookie> ReadCookies(JsonElement result)
    {
        var output = new List<FlowSessionCookie>();
        if (!result.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array) return output.AsReadOnly();
        foreach (var cookie in cookies.EnumerateArray())
        {
            var name = ReadString(cookie, "name");
            var value = ReadString(cookie, "value");
            var domain = ReadString(cookie, "domain");
            var path = ReadString(cookie, "path");
            if (name is null || !AuthenticationCookieNames.Contains(name) || value is null || value.Length is 0 or > 16384 ||
                value.Any(c => char.IsControl(c) || c == ';') ||
                domain is not (".google.com" or "google.com" or "flow.google.com" or ".flow.google.com" or "aisandbox-pa.googleapis.com") ||
                path is null || path.Length > 1024 || !path.StartsWith('/') || path.Any(char.IsControl)) continue;
            double? expires = cookie.TryGetProperty("expires", out var expiry) && expiry.TryGetDouble(out var seconds) ? seconds : null;
            output.Add(new FlowSessionCookie(name, value, domain, path, expires,
                ReadBoolean(cookie, "secure"), ReadBoolean(cookie, "httpOnly"), ReadString(cookie, "sameSite")));
            if (output.Count > 128) throw new FlowBrowserLoginException("Окно входа вернуло слишком много данных сеанса.");
        }
        return output.AsReadOnly();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsDevToolsPath(string path, string prefix) => path.StartsWith(prefix, StringComparison.Ordinal) &&
        path.Length > prefix.Length && path.Length <= prefix.Length + 128 &&
        path.AsSpan(prefix.Length).ToArray().All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static Uri PageEndpoint(Uri browserEndpoint, string targetId)
    {
        var endpoint = new UriBuilder(browserEndpoint) { Path = "/devtools/page/" + targetId }.Uri;
        ValidateEndpoint(endpoint, browserEndpoint.Port, "/devtools/page/");
        return endpoint;
    }

    private static void ValidateEndpoint(Uri endpoint, int expectedPort, string prefix)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" || endpoint.Port != expectedPort ||
            endpoint.Port is < 1024 or > 65535 || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            !IsDevToolsPath(endpoint.AbsolutePath, prefix))
            throw new FlowBrowserLoginException("Недопустимый адрес локального окна входа.");
    }

    private static void EnsureBrowserOpen(Process process)
    {
        if (process.HasExited)
            throw new FlowBrowserLoginException("Окно входа Google закрыто. Повторите вход, когда будете готовы.");
    }

    private static async Task<bool> CloseManualBrowserAsync(Process process)
    {
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (!process.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                process.Refresh();
                _ = process.CloseMainWindow();
                using var closeWait = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await process.WaitForExitAsync(closeWait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            return process.HasExited;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<bool> CloseOwnedBrowserAsync(Process process, CdpConnection? browser)
    {
        if (browser is not null)
        {
            using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await browser.InvokeAsync("Browser.close", new { }, closeDeadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or CdpCommandException or IOException) { }
        }
        try
        {
            if (process.HasExited) return true;
            using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (!process.HasExited)
            {
                // Only the exact process launched above and its descendants are eligible.
                process.Kill(entireProcessTree: true);
                using var killDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(killDeadline.Token).ConfigureAwait(false);
            }
            return process.HasExited;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException or NotSupportedException) { return false; }
    }

    private sealed class CdpCommandException : Exception;

    /// <summary>Sequential CDP requests, bounded replies, and no payload or error logging.</summary>
    private sealed class CdpConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private int _nextId;

        private CdpConnection() { _socket.Options.Proxy = null; _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20); }

        public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            ValidateEndpoint(endpoint, endpoint.Port, endpoint.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal)
                ? "/devtools/browser/" : "/devtools/page/");
            var connection = new CdpConnection();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await connection._socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                return connection;
            }
            catch { connection._socket.Dispose(); throw; }
        }

        public async Task<JsonElement> InvokeAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var id = ++_nextId;
            var message = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await _socket.SendAsync(message.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            var buffer = new byte[16384];
            while (true)
            {
                using var reply = new MemoryStream();
                ValueWebSocketReceiveResult part;
                do
                {
                    part = await _socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                    if (part.MessageType != WebSocketMessageType.Text) throw new CdpCommandException();
                    if (reply.Length + part.Count > 512 * 1024) throw new CdpCommandException();
                    reply.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var document = JsonDocument.Parse(reply.GetBuffer().AsMemory(0, checked((int)reply.Length)));
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var replyId) || !replyId.TryGetInt32(out var actualId) || actualId != id) continue;
                if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result)) throw new CdpCommandException();
                return result.Clone();
            }
        }

        public ValueTask DisposeAsync() { _socket.Dispose(); return ValueTask.CompletedTask; }
    }
}
