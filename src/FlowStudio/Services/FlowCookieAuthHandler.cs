using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

/// <summary>
/// Adapts explicit Flow RPC calls to the current first-party BOQ transport using
/// only the session captured by this app's dedicated login browser. No browser
/// or JavaScript runs while this handler sends requests. Google may reject an
/// expired/ineligible session; this handler never retries a paid request.
/// </summary>
public sealed class FlowCookieAuthHandler : DelegatingHandler
{
    public const string SessionCredentialMarker = "flow-cookie-session";
    const int MaxBodyBytes = 16 * 1024 * 1024;
    const string FlowOrigin = "https://flow.google.com";
    const string BatchPath = "/_/AiSandboxAngularFrontend/data/batchexecute";
    static readonly IReadOnlyDictionary<string, string> Methods = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/GetModels"] = "HTrJv",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/BatchGenerateImages"] = "ogiZ0b",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/GetMedia"] = "as29s",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/GetProjects"] = "UpteDb",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/GetProjectContents"] = "Zzl0ze",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/UploadImage"] = "maseQ",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/UpdateWorkflow"] = "mYWVGd",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/UpdateCollection"] = "O2COMe",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/CreateCollection"] = "Uxbujd",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/BatchMoveToCollection"] = "kVdhHf",
        ["google.internal.labs.aisandbox.proto.flow.v1.FlowService/BatchDeleteAssets"] = "cz8Z4b",
        ["google.internal.labs.aisandbox.proto.flow.agent.v1.FlowCreationAgentService/ListSessions"] = "mrlkwd",
        ["google.internal.labs.aisandbox.proto.flow.agent.v1.FlowCreationAgentService/GetSession"] = "GN0Bre",
        ["google.internal.labs.aisandbox.proto.flow.agent.v1.FlowCreationAgentService/CreateSession"] = "csbIsb",
        ["google.internal.labs.aisandbox.proto.videofx.v1.VideoFxService/BatchAsyncGenerateVideoText"] = "YhhmEf",
        ["google.internal.labs.aisandbox.proto.videofx.v1.VideoFxService/BatchAsyncGenerateVideoStartImage"] = "eb1hJf",
        ["google.internal.labs.aisandbox.proto.videofx.v1.VideoFxService/BatchAsyncGenerateVideoStartAndEndImage"] = "nprQif",
        ["google.internal.labs.aisandbox.proto.videofx.v1.VideoFxService/BatchAsyncGenerateVideoReferenceImages"] = "MZZa6b",
        ["google.internal.labs.aisandbox.proto.videofx.v1.VideoFxService/BatchCheckAsyncVideoGenerationStatus"] = "jwpduf",
        ["google.internal.labs.aisandbox.proto.videofx.v1.VideoFxService/GetCredits"] = "nzlxg",
        ["google.internal.labs.aisandbox.proto.videofx.v1.VideoFxService/GetFlowAppConfig"] = "cPZSdc",
        ["google.internal.labs.aisandbox.v1.AiSandbox/CreateProject"] = "jHPbke"
        ,["google.internal.labs.aisandbox.v1.AiSandbox/GetProject"] = "ngNC2"
        ,["google.internal.labs.aisandbox.v1.AiSandbox/UpdateAgentInfo"] = "Kcr7Ub"
        ,["google.internal.labs.aisandbox.v1.AiSandbox/UpdateProjectInfo"] = "o8DA4"
        ,["google.internal.labs.aisandbox.v1.AiSandbox/DeleteProject"] = "QI2zvc"
    };
    static readonly HashSet<string> AuthenticationCookieNames = new(StringComparer.Ordinal)
    {
        "SAPISID", "APISID", "SID", "HSID", "SSID", "SIDCC", "OSID", "__Secure-OSID",
        "__Secure-1PAPISID", "__Secure-3PAPISID", "__Secure-1PSID", "__Secure-3PSID",
        "__Secure-1PSIDTS", "__Secure-3PSIDTS", "__Secure-1PSIDCC", "__Secure-3PSIDCC"
    };

    FlowBrowserSession? session;
    int lastResponseSetCookieCount;
    int lastResponseCookieChanges;
    public FlowBrowserSession? Session { get => Volatile.Read(ref session); set => Volatile.Write(ref session, value); }
    /// <summary>Safe diagnostic counts only; cookie names and values are never logged.</summary>
    public int LastResponseSetCookieCount => Volatile.Read(ref lastResponseSetCookieCount);
    public int LastResponseCookieChanges => Volatile.Read(ref lastResponseCookieChanges);

    // innerHandler must have AllowAutoRedirect=false and UseCookies=false. Cookie
    // routing is done below, so neither redirects nor a shared jar can leak it.
    public FlowCookieAuthHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != SessionCredentialMarker)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var captured = Session ?? throw new InvalidOperationException("Войдите в Google через кнопку входа приложения.");
        var endpoint = request.RequestUri;
        if (request.Method != HttpMethod.Post || endpoint is null || endpoint.Scheme != "https" || endpoint.Port != 443 ||
            endpoint.Host != "aisandbox-pa.googleapis.com" || !string.IsNullOrEmpty(endpoint.Query) ||
            !endpoint.AbsolutePath.StartsWith("/$rpc/", StringComparison.Ordinal) ||
            !Methods.TryGetValue(endpoint.AbsolutePath[6..], out var rpcId))
            throw new InvalidOperationException("Сессия Google разрешена только для известных методов Flow.");

        var xsrf = WizValue(captured, "SNlM0e");
        if (string.IsNullOrWhiteSpace(xsrf))
            throw new InvalidOperationException("Сессия Google не содержит авторизации Flow. Выполните вход повторно.");
        if (request.Content is null) throw new InvalidOperationException("Отсутствует запрос Flow.");
        var payload = await ReadBoundedAsync(request.Content, cancellationToken).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(payload))
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Запрос Flow имеет неподдерживаемый формат.");

        var query = new Dictionary<string, string> { ["rpcids"] = rpcId, ["source-path"] = "/", ["rt"] = "c" };
        if (WizValue(captured, "FdrFJe") is { Length: > 0 } flowSessionId) query["f.sid"] = flowSessionId;
        if (WizValue(captured, "cfb2h") is { Length: > 0 } build) query["bl"] = build;
        var uri = new Uri(FlowOrigin + BatchPath + "?" + string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        var cookieHeader = BuildCookieHeader(captured.Cookies, uri, DateTimeOffset.UtcNow);
        if (cookieHeader.Length == 0)
            throw new InvalidOperationException("Сессия Google истекла. Выполните вход повторно.");

        using var native = new HttpRequestMessage(HttpMethod.Post, uri);
        native.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        native.Headers.TryAddWithoutValidation("Origin", FlowOrigin);
        native.Headers.Referrer = new Uri(FlowOrigin + "/");
        if (captured.UserAgent.Length is > 0 and < 1024 && !captured.UserAgent.AsSpan().ContainsAny('\r', '\n', '\0'))
            native.Headers.TryAddWithoutValidation("User-Agent", captured.UserAgent);
        native.Headers.TryAddWithoutValidation("X-Same-Domain", "1");
        var envelope = new object?[] { new object?[] { new object?[] { rpcId, payload, null, "generic" } } };
        native.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f.req"] = JsonSerializer.Serialize(envelope), ["at"] = xsrf });
        using var response = await base.SendAsync(native, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
            return Error(request, HttpStatusCode.Unauthorized, 16);
        if(response.Headers.TryGetValues("Set-Cookie",out var rotated))MergeResponseCookies(captured,uri,rotated);
        else {Volatile.Write(ref lastResponseSetCookieCount,0);Volatile.Write(ref lastResponseCookieChanges,0);}
        if (!response.IsSuccessStatusCode)
        {
            string? reason = null;
            if (!IsRedirect(response.StatusCode))
            {
                try
                {
                    var errorBody = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                    var decoded = DecodeBatchResponse(errorBody, rpcId);
                    reason = decoded.FailureReason;
                    if (reason is null)
                    {
                        using var errorJson = JsonDocument.Parse(errorBody);
                        if (errorJson.RootElement.ValueKind == JsonValueKind.Array && errorJson.RootElement.GetArrayLength() >= 2 && errorJson.RootElement[0].ValueKind == JsonValueKind.Number)
                            reason = ClassifyFailure(errorJson.RootElement);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or DecoderFallbackException) { }
            }
            return Error(request, IsRedirect(response.StatusCode) ? HttpStatusCode.Unauthorized : response.StatusCode, CanonicalCode(response.StatusCode), reason);
        }

        try
        {
            var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var decoded = DecodeBatchResponse(body, rpcId);
            if (decoded.ErrorCode is { } code) return Error(request, HttpCode(code), code, decoded.FailureReason);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(decoded.Payload!, Encoding.UTF8, "application/json+protobuf")
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or DecoderFallbackException)
        {
            return Error(request, HttpStatusCode.BadGateway, 13);
        }
    }

    static string? WizValue(FlowBrowserSession value, string key) => value.Wiz.TryGetValue(key, out var result) && result?.Length <= 16384 ? result : null;

    internal void MergeResponseCookies(FlowBrowserSession captured,Uri origin,IEnumerable<string> headers)
    {
        var additions=headers.Take(128).ToArray();
        Volatile.Write(ref lastResponseSetCookieCount,additions.Length);
        Volatile.Write(ref lastResponseCookieChanges,0);
        if(origin.Scheme!="https"||origin.Host!="flow.google.com"||!origin.IsDefaultPort||origin.UserInfo.Length>0||!ReferenceEquals(Session,captured))return;
        var cookies=captured.Cookies.ToList();
        foreach(var addition in additions)
        {
            if(addition.Length>32768)continue;
            var separator=addition.IndexOf('=');if(separator<=0)continue;
            var name=addition[..separator].Trim();
            if(!AuthenticationCookieNames.Contains(name)||!captured.Cookies.Any(cookie=>cookie.Name==name))continue;
            FlowSessionRefreshService.MergeCookie(cookies,origin,addition);
        }
        if(cookies.SequenceEqual(captured.Cookies))return;
        var changed=cookies.Count(cookie=>!captured.Cookies.Contains(cookie))+captured.Cookies.Count(cookie=>!cookies.Contains(cookie));
        var updated=captured with {Cookies=cookies.AsReadOnly()};
        // A late response from an old login cannot restore that account after a
        // switch or disconnect. No network request is retried when CAS fails.
        if(ReferenceEquals(Interlocked.CompareExchange(ref session,updated,captured),captured))
            Volatile.Write(ref lastResponseCookieChanges,changed);
    }

    internal static string BuildCookieHeader(IReadOnlyList<FlowSessionCookie> cookies, Uri target, DateTimeOffset now)
    {
        // This app's exported jar may contain related Google API cookies. Only
        // cookies that really match the fixed same-origin Flow endpoint are sent.
        if (target.Scheme != "https" || target.Host != "flow.google.com" || target.Port != 443)
            throw new InvalidOperationException("Cookies Google разрешены только для Flow.");
        var matches = new List<FlowSessionCookie>();
        foreach (var cookie in cookies)
        {
            var domain = cookie.Domain.TrimStart('.');
            if (domain != "google.com" && domain != "flow.google.com") continue;
            if (cookie.Domain[0] != '.' && domain != target.Host) continue;
            var path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
            if (!target.AbsolutePath.StartsWith(path, StringComparison.Ordinal) ||
                (target.AbsolutePath.Length > path.Length && !path.EndsWith('/') && target.AbsolutePath[path.Length] != '/')) continue;
            if (cookie.Expires is > 0 && cookie.Expires <= now.ToUnixTimeSeconds()) continue;
            if (cookie.Name.Length is < 1 or > 256 || cookie.Value.Length > 16384 ||
                cookie.Name.Any(c => c <= 32 || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c)) ||
                cookie.Value.Any(c => c < 32 || c >= 127 || c is ';' or ',')) continue;
            matches.Add(cookie);
        }
        var result = string.Join("; ", matches.OrderByDescending(c => c.Path.Length).Select(c => c.Name + "=" + c.Value));
        if (result.Length > 64 * 1024) throw new InvalidOperationException("Сессия Google превышает допустимый размер.");
        return result;
    }

    internal sealed record BatchResult(string? Payload, int? ErrorCode, string? FailureReason = null);

    internal static BatchResult DecodeBatchResponse(string body, string rpcId)
    {
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes) throw new InvalidDataException("Response too large.");
        var span = body.AsSpan().TrimStart();
        if (span.StartsWith(")]}'"))
        {
            var end = span.IndexOf('\n');
            if (end < 0) throw new InvalidDataException("Incomplete response.");
            span = span[(end + 1)..].TrimStart();
        }
        BatchResult? found = null;
        // rt=c uses length-prefixed JSON frames. Accept the unframed form too;
        // each frame is parsed independently and trailing partial data fails shut.
        while (!span.IsEmpty)
        {
            span = span.TrimStart();
            if (span.IsEmpty) break;
            string frame;
            if (char.IsAsciiDigit(span[0]))
            {
                var end = span.IndexOf('\n');
                if (end <= 0 || !int.TryParse(span[..end].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 1 || length > MaxBodyBytes)
                    throw new InvalidDataException("Invalid frame length.");
                // Current public Closure gLa uses b.substr(newlineIndex, length),
                // so the count is UTF-16 code units INCLUDING the leading newline.
                // Confirmed against a live anonymous rt=c GetModels response.
                span = span[end..];
                if (span.Length < length) throw new InvalidDataException("Incomplete frame.");
                frame = span[..length].ToString();
                span = span[length..];
            }
            else { frame = span.ToString(); span = default; }
            using var document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 96 });
            foreach (var row in Rows(document.RootElement))
            {
                if (row.GetArrayLength() < 2 || row[0].ValueKind != JsonValueKind.String) continue;
                if (row[0].GetString() == "er")
                {
                    // Anonymous live GetModels: HTTP status field5, canonical field9.
                    var code = row.GetArrayLength() > 9 && row[9].ValueKind == JsonValueKind.Number && row[9].TryGetInt32(out var canonical) ? canonical : 13;
                    return new(null, code, ClassifyFailure(row));
                }
                if (row[0].GetString() != "wrb.fr" || row[1].ValueKind != JsonValueKind.String || row[1].GetString() != rpcId) continue;
                if (found is not null) throw new InvalidDataException("Duplicate response.");
                // Rr.getError is field 5; the 'wrb.fr' message sentinel shifts fields by 1.
                if (row.GetArrayLength() > 5 && row[5].ValueKind == JsonValueKind.Array && row[5].GetArrayLength() > 0 && row[5][0].ValueKind == JsonValueKind.Number && row[5][0].TryGetInt32(out var status) && status != 0)
                    found = new(null, status, ClassifyFailure(row[5]));
                else if (row.GetArrayLength() > 2 && row[2].ValueKind == JsonValueKind.String)
                {
                    var payload = row[2].GetString()!;
                    using var inner = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 96 });
                    found = inner.RootElement.ValueKind == JsonValueKind.Array ? new(payload, null) : new(null, 16);
                }
                else found = new(null, 16);
            }
        }
        return found ?? new(null, 16);
    }

    static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid batch response.");
        if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.String) { yield return root; yield break; }
        foreach (var row in root.EnumerateArray()) if (row.ValueKind == JsonValueKind.Array) yield return row;
    }

    static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxBodyBytes) throw new InvalidDataException("Response too large.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxBodyBytes) throw new InvalidDataException("Response too large.");
            output.Write(chunk, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    static bool IsRedirect(HttpStatusCode code) => (int)code is >= 300 and < 400;
    static int CanonicalCode(HttpStatusCode code) => (int)code switch { 400 => 3, 401 => 16, 403 => 7, 404 => 5, 429 => 8, 503 => 14, _ => 13 };
    static HttpStatusCode HttpCode(int code) => code switch { 3 => HttpStatusCode.BadRequest, 5 => HttpStatusCode.NotFound, 7 => HttpStatusCode.Forbidden, 8 => HttpStatusCode.TooManyRequests, 16 => HttpStatusCode.Unauthorized, 14 => HttpStatusCode.ServiceUnavailable, _ => HttpStatusCode.BadGateway };
    internal static string? ClassifyFailure(JsonElement status)
    {
        // Inspect structured error fields only. Arbitrary server text never leaves
        // this classifier, so a response cannot echo session data into the UI/log.
        if (status.ValueKind == JsonValueKind.String)
        {
            var text = status.GetString() ?? "";
            if (text.Contains("captcha", StringComparison.OrdinalIgnoreCase)) return "captcha";
            if (text.Contains("UNSUPPORTED_COUNTRY", StringComparison.OrdinalIgnoreCase) || text.Contains("COUNTRY_NOT_SUPPORTED", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("unsupported region", StringComparison.OrdinalIgnoreCase)) return "country";
            if (text.Contains("INSUFFICIENT_CREDIT", StringComparison.OrdinalIgnoreCase) || text.Contains("not enough credit", StringComparison.OrdinalIgnoreCase)) return "credits";
            if (text.Contains("AGE_RESTRICT", StringComparison.OrdinalIgnoreCase) || text.Contains("AGE_NOT_SUPPORTED", StringComparison.OrdinalIgnoreCase)) return "age";
        }
        if (status.ValueKind == JsonValueKind.Array)
            foreach (var item in status.EnumerateArray()) if (ClassifyFailure(item) is { } reason) return reason;
        if (status.ValueKind == JsonValueKind.Object)
            foreach (var property in status.EnumerateObject()) if (ClassifyFailure(property.Value) is { } reason) return reason;
        return null;
    }

    static HttpResponseMessage Error(HttpRequestMessage request, HttpStatusCode httpStatus, int code, string? failureReason = null)
    {
        // Never relay arbitrary HTML, error details, cookies or CSRF/session data.
        var message = failureReason switch
        {
            "captcha" => "Google требует новую проверку reCAPTCHA для этого действия. Браузер для генерации не запускается; запрос автоматически не повторяется.",
            "country" => "Google сообщил, что Flow недоступен для страны этого аккаунта или подключения.",
            "credits" => "Google сообщил, что для действия недостаточно кредитов Flow.",
            "age" => "Google сообщил о возрастном ограничении аккаунта Flow.",
            _ => code switch
        {
            16 => "Сессия Google не принята или истекла. Выполните вход повторно.",
            7 => "Google отклонил доступ Flow. Проверьте доступность аккаунта и региона.",
            8 => "Google сообщил о лимите запросов или кредитов Flow.",
            3 => "Google отклонил запрос Flow. Проверьте выбранные настройки.",
            5 => "Google изменил маршрут Flow или объект больше не доступен.",
            _ => "Flow вернул неполный ответ или ошибку сервиса. Задание автоматически не повторялось."
            }
        };
        return new HttpResponseMessage(httpStatus) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new object?[] { code, message }), Encoding.UTF8, "application/json+protobuf") };
    }
}
