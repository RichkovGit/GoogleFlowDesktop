using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

/// <summary>Synthetic login checks: no browser, profile, account, or external network is used.</summary>
public static class FlowBrowserLoginSelfTest
{
    public static async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var service = typeof(FlowBrowserLoginService);
        var assertions = 0;
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        object? Call(string method, params object[] args) =>
            service.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);

        var syntheticProfile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlowStudio-Synthetic-LoginProfile");
        var normalStart = FlowBrowserLoginService.BuildBrowserStartInfo("synthetic-chrome.exe", syntheticProfile, captureSession: false);
        var captureStart = FlowBrowserLoginService.BuildBrowserStartInfo("synthetic-chrome.exe", syntheticProfile, captureSession: true);
        Require(!normalStart.ArgumentList.Any(argument => argument.Contains("remote-debugging", StringComparison.Ordinal) ||
            argument.Contains("automation", StringComparison.OrdinalIgnoreCase) || argument.Contains("headless", StringComparison.Ordinal)),
            "Manual Google sign-in launch contains automation or DevTools flags.");
        Require(captureStart.ArgumentList.Contains("--remote-debugging-port=0") &&
            captureStart.ArgumentList.Contains("--remote-debugging-address=127.0.0.1"),
            "Saved-session handoff lacks the restricted loopback endpoint.");
        Require(normalStart.ArgumentList.Contains("--user-data-dir=" + syntheticProfile) &&
            captureStart.ArgumentList.Contains("--user-data-dir=" + syntheticProfile) &&
            normalStart.ArgumentList.Last() == "https://flow.google.com/" &&
            captureStart.ArgumentList.Last() == "https://flow.google.com/",
            "The sign-in and handoff stages do not use the same dedicated Flow profile.");
        Require(!normalStart.ArgumentList.Concat(captureStart.ArgumentList).Any(argument =>
            argument.Contains("AutomationControlled", StringComparison.Ordinal) ||
            argument.StartsWith("--user-agent", StringComparison.Ordinal) || argument.StartsWith("--headless", StringComparison.Ordinal)),
            "Sign-in arguments contain browser identity spoofing.");

        var login = new FlowBrowserLoginService();
        Require(!login.AwaitingManualConfirmation && !login.IsBrowserRunning, "A new login service started browser work automatically.");
        login.ConfirmManualSignIn(); // A click before the manual stage must not authorize a future login.
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationField = service.GetField("_manualConfirmation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        confirmationField.SetValue(login, confirmation);
        Require(login.AwaitingManualConfirmation && !confirmation.Task.IsCompleted, "Manual confirmation was bypassed.");
        login.ConfirmManualSignIn();
        Require(confirmation.Task.IsCompletedSuccessfully && confirmation.Task.Result, "Explicit manual confirmation did not release the handoff.");
        confirmationField.SetValue(login, null);
        Require(!login.AwaitingManualConfirmation, "Manual confirmation state was not cleared.");

        Call("ValidateEndpoint", new Uri("ws://127.0.0.1:19222/devtools/browser/abc-123"), 19222, "/devtools/browser/");
        assertions++;
        foreach (var address in new[]
        {
            "ws://localhost:19222/devtools/browser/a",
            "ws://127.0.0.2:19222/devtools/browser/a",
            "wss://127.0.0.1:19222/devtools/browser/a",
            "ws://127.0.0.1:19222/devtools/page/a",
            "ws://127.0.0.1:19222/devtools/browser/a?q=x",
            "ws://user@127.0.0.1:19222/devtools/browser/a",
            "ws://127.0.0.1:19222/devtools/browser/a%2Fother",
            "ws://127.0.0.1:1023/devtools/browser/a"
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rejected = false;
            try { Call("ValidateEndpoint", new Uri(address), 19222, "/devtools/browser/"); }
            catch (TargetInvocationException error) when (error.InnerException is FlowBrowserLoginException) { rejected = true; }
            Require(rejected, "Login endpoint validation accepted an unsafe local address.");
        }
        var wrongPortRejected = false;
        try { Call("ValidateEndpoint", new Uri("ws://127.0.0.1:19223/devtools/browser/a"), 19222, "/devtools/browser/"); }
        catch (TargetInvocationException error) when (error.InnerException is FlowBrowserLoginException) { wrongPortRejected = true; }
        Require(wrongPortRejected, "Login endpoint validation accepted another port.");

        var targets = JsonSerializer.SerializeToElement(new
        {
            targetInfos = new[]
            {
                new { type = "page", url = "https://accounts.google.com/", targetId = "wrong1" },
                new { type = "page", url = "https://flow.google.com.evil.test/", targetId = "wrong2" },
                new { type = "page", url = "https://flow.google.com:444/", targetId = "wrong3" },
                new { type = "page", url = "http://flow.google.com/", targetId = "wrong4" },
                new { type = "service_worker", url = "https://flow.google.com/", targetId = "wrong5" },
                new { type = "page", url = "https://flow.google.com/", targetId = "correct" }
            }
        });
        Require((string?)Call("FindFlowTarget", targets) == "correct", "Login selected a target outside the HTTPS Flow page.");

        var fixtures = JsonSerializer.SerializeToElement(new
        {
            cookies = new[]
            {
                new { name = "SAPISID", value = "synthetic-test-value", domain = ".google.com", path = "/", secure = true, httpOnly = false, expires = -1, sameSite = "None" },
                new { name = "NID", value = "synthetic-ad-cookie", domain = ".google.com", path = "/", secure = true, httpOnly = false, expires = -1, sameSite = "None" },
                new { name = "SAPISID", value = "unrelated", domain = ".example.com", path = "/", secure = true, httpOnly = false, expires = -1, sameSite = "None" },
                new { name = "SAPISID", value = "bad\r\nheader", domain = ".google.com", path = "/", secure = true, httpOnly = false, expires = -1, sameSite = "None" },
                new { name = "SAPISID", value = "bad;header", domain = ".google.com", path = "/", secure = true, httpOnly = false, expires = -1, sameSite = "None" },
                new { name = "SAPISID", value = "bad-domain", domain = ".google.com.evil.test", path = "/", secure = true, httpOnly = false, expires = -1, sameSite = "None" },
                new { name = "SAPISID", value = "bad-path", domain = ".google.com", path = "/\r\nheader", secure = true, httpOnly = false, expires = -1, sameSite = "None" }
            }
        });
        var cookies = (IReadOnlyList<FlowSessionCookie>)Call("ReadCookies", fixtures)!;
        Require(cookies.Count == 1 && cookies[0].Name == "SAPISID" && cookies[0].Value == "synthetic-test-value",
            "Login cookie allowlist retained unrelated cookies or header injection.");

        var wizJson = JsonSerializer.Serialize(new
        {
            awbSEf = true, SNlM0e = "synthetic-csrf", S06Grb = "synthetic-session",
            cfb2h = "synthetic-build", FdrFJe = "synthetic-fsid", xZbWve = "public-key",
            unrelatedSecret = "do-not-include"
        });
        var evaluation = JsonSerializer.SerializeToElement(new { result = new { type = "string", value = wizJson } });
        var wiz = (IReadOnlyDictionary<string, string?>)Call("ReadWiz", evaluation)!;
        Require(wiz.Count == 6 && wiz["awbSEf"] == "true" && wiz["FdrFJe"] == "synthetic-fsid" &&
            !wiz.ContainsKey("unrelatedSecret"), "Login read bootstrap keys outside the explicit allowlist.");
        var session = new FlowBrowserSession(cookies, wiz, "SyntheticUA", DateTimeOffset.UtcNow);
        Require(session.HasGoogleSession, "Synthetic session should pass the local metadata gate.");
        var signedOut = new FlowBrowserSession(cookies, new Dictionary<string, string?> { ["awbSEf"] = "false" }, "SyntheticUA", DateTimeOffset.UtcNow);
        Require(!signedOut.HasGoogleSession, "Signed-out metadata passed the local session gate.");
        Require(!session.ToString().Contains("synthetic", StringComparison.Ordinal) &&
            !cookies[0].ToString().Contains("synthetic-test-value", StringComparison.Ordinal),
            "Login record ToString exposed credential contents.");
        var roundTrip = JsonSerializer.Deserialize<FlowBrowserSession>(JsonSerializer.Serialize(session))!;
        Require(roundTrip.Cookies[0].Value == "synthetic-test-value" && roundTrip.Wiz["FdrFJe"] == "synthetic-fsid",
            "Login session JSON does not preserve the encrypted-storage payload.");

        await TestCdpFramesAsync(service, cancellationToken).ConfigureAwait(false);
        assertions++;
        return $"{assertions} synthetic checks: ordinary manual-browser launch, explicit confirmation, isolated session handoff, loopback endpoints, exact Flow origin, minimum auth cookies, WIZ whitelist, redaction, session serialization, fragmented CDP. Browser and user credentials were not accessed.";
    }

    private static async Task TestCdpFramesAsync(Type service, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var request = new List<byte>();
            var single = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(single, token).ConfigureAwait(false) == 0)
                    throw new InvalidOperationException("Mock CDP handshake disconnected.");
                request.Add(single[0]);
                if (request.Count >= 4 && request[^4] == 13 && request[^3] == 10 && request[^2] == 13 && request[^1] == 10) break;
                if (request.Count > 8192) throw new InvalidOperationException("Mock CDP handshake exceeded its limit.");
            }
            var headers = Encoding.ASCII.GetString(request.ToArray());
            var key = headers.Split("\r\n").First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), token).ConfigureAwait(false);
            using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(20));
            var buffer = new byte[8192];
            var received = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
            var id = document.RootElement.GetProperty("id").GetInt32();
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"method\":\"Test.event\"}").AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            var reply = JsonSerializer.SerializeToUtf8Bytes(new { id, result = new { ok = true } });
            await socket.SendAsync(reply.AsMemory(0, 7), WebSocketMessageType.Text, false, token).ConfigureAwait(false);
            await socket.SendAsync(reply.AsMemory(7), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }, token);
        IAsyncDisposable? connection = null;
        try
        {
            var type = service.GetNestedType("CdpConnection", BindingFlags.NonPublic)!;
            var connect = (Task)type.GetMethod("ConnectAsync", BindingFlags.Public | BindingFlags.Static)!.Invoke(null,
                new object[] { new Uri($"ws://127.0.0.1:{port}/devtools/page/test"), token })!;
            await connect.ConfigureAwait(false);
            connection = (IAsyncDisposable)connect.GetType().GetProperty("Result")!.GetValue(connect)!;
            var invoke = (Task)type.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Instance)!.Invoke(connection,
                new object[] { "Synthetic.command", new { }, token })!;
            await invoke.ConfigureAwait(false);
            var result = (JsonElement)invoke.GetType().GetProperty("Result")!.GetValue(invoke)!;
            if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("CDP did not reassemble its response.");
            await server.ConfigureAwait(false);
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            listener.Stop();
            // Observe mock server errors even when the client failed first.
            if (!server.IsCompleted) deadline.Cancel();
            try { await server.ConfigureAwait(false); }
            catch (Exception) when (server.IsCanceled || server.IsFaulted) { }
        }
    }
}
