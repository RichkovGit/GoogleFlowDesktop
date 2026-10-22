using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FlowStudio.Services;

/// <summary>Protocol fixtures use synthetic data only; no Google account, browser or network.</summary>
internal static class FlowCookieAuthSelfTest
{
    public static async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        const string payload = "[[null,null,null,null,[],[]]]";
        const string secret = "synthetic-cookie-never-a-real-account";
        const string xsrf = "synthetic-xsrf-never-a-real-session";
        var now = DateTimeOffset.UtcNow;
        var session = new FlowBrowserSession(
            [new("SID", secret, ".google.com", "/", now.AddHours(1).ToUnixTimeSeconds(), true, true, "None"),
             new("WRONG_PATH", "excluded", ".google.com", "/login", null, true, true, null),
             new("WRONG_HOST", "excluded", "aisandbox-pa.googleapis.com", "/", null, true, true, null),
             new("EXPIRED", "excluded", ".google.com", "/", now.AddMinutes(-1).ToUnixTimeSeconds(), true, true, null)],
            new Dictionary<string, string?> { ["SNlM0e"] = xsrf, ["FdrFJe"] = "correct-fsid", ["S06Grb"] = "not-fsid", ["cfb2h"] = "synthetic-build" },
            "FlowStudioProtocolFixture", now);
        var calls = 0;
        using var adapter = new FlowCookieAuthHandler(new FixtureHandler(async (request, token) =>
        {
            calls++;
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing native URI.");
            Require(uri.Host == "flow.google.com", "Session escaped Flow host.");
            Require(uri.AbsolutePath == "/_/AiSandboxAngularFrontend/data/batchexecute", "Wrong BOQ path.");
            Require(uri.Query.Contains("rpcids=HTrJv") && uri.Query.Contains("f.sid=correct-fsid") && !uri.Query.Contains("not-fsid"), "Wrong RPC/session mapping.");
            Require(request.Headers.Authorization is null, "Sent bearer marker as a real credential.");
            Require(request.Headers.GetValues("Cookie").Single() == "SID=" + secret, "Cookie domain/path/expiry routing failed.");
            var form = (await request.Content!.ReadAsStringAsync(token)).Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0].Replace('+', ' ')), part => Uri.UnescapeDataString(part[1].Replace('+', ' ')));
            Require(form["at"] == xsrf, "Missing BOQ XSRF token.");
            using var envelope = JsonDocument.Parse(form["f.req"]);
            Require(envelope.RootElement[0][0][0].GetString() == "HTrJv" && envelope.RootElement[0][0][1].GetString() == "[]", "Wrong RPC request envelope.");
            var fixtureResponse=new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(Frame("HTrJv", payload), Encoding.UTF8) };
            fixtureResponse.Headers.TryAddWithoutValidation("Set-Cookie","SID=synthetic-rotated-cookie; Domain=.google.com; Path=/; Secure; HttpOnly");
            return fixtureResponse;
        })) { Session = session };
        using var client = new HttpClient(adapter);
        using var rpc = new HttpRequestMessage(HttpMethod.Post, FlowModelCatalogService.Endpoint);
        rpc.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FlowCookieAuthHandler.SessionCredentialMarker);
        rpc.Content = new StringContent("[]");
        using var response = await client.SendAsync(rpc, cancellationToken);
        Require(response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync(cancellationToken) == payload, "Did not unwrap native response.");
        Require(response.RequestMessage == rpc && calls == 1, "RPC identity changed or duplicate submission occurred.");
        Require(adapter.Session?.Cookies.Single(c=>c.Name=="SID").Value=="synthetic-rotated-cookie"&&adapter.LastResponseSetCookieCount==1&&adapter.LastResponseCookieChanges>0,"Native response cookie rotation was discarded.");
        var rotated=adapter.Session!;
        adapter.MergeResponseCookies(rotated,new Uri("https://flow.google.com/"),["SID=foreign-cookie; Domain=example.com; Path=/; Secure","UNLISTED=excluded; Domain=.google.com; Path=/; Secure"]);
        Require(ReferenceEquals(adapter.Session,rotated),"Invalid-domain or non-allowlisted cookie changed the app session.");
        adapter.MergeResponseCookies(rotated,new Uri("https://example.com/"),["SID=foreign-origin; Domain=.google.com; Path=/; Secure"]);
        Require(ReferenceEquals(adapter.Session,rotated),"Foreign response origin changed the app session.");
        adapter.Session=session;
        adapter.MergeResponseCookies(rotated,new Uri("https://flow.google.com/"),["SID=late-response; Domain=.google.com; Path=/; Secure"]);
        Require(ReferenceEquals(adapter.Session,session),"Late old-account response replaced a newly selected session.");

        var unicode = "[\"Настоящий кадр\"]";
        Require(FlowCookieAuthHandler.DecodeBatchResponse(Frame("HTrJv", unicode), "HTrJv").Payload == unicode, "UTF-16 frame lengths broken.");
        // Exact anonymous live Google rt=c response shape and framing, no credentials.
        const string liveAnonymous = ")]}'\n\n104\n[[\"er\",null,null,null,null,401,null,null,null,16],[\"di\",21],[\"af.httprm\",21,\"-6585318309733074937\",8]]\n25\n[[\"e\",4,null,null,140]]\n";
        Require(FlowCookieAuthHandler.DecodeBatchResponse(liveAnonymous, "HTrJv").ErrorCode == 16, "Live Google anonymous envelope was not recognized.");
        var errorJson = JsonSerializer.Serialize(new object?[] { new object?[] { "wrb.fr", "HTrJv", null, null, null, new object?[] { 7, secret + xsrf } } });
        Require(FlowCookieAuthHandler.DecodeBatchResponse(errorJson, "HTrJv").ErrorCode == 7, "Canonical error not recognized.");
        var captchaError = JsonSerializer.Serialize(new object?[] { new object?[] { "wrb.fr", "HTrJv", null, null, null, new object?[] { 7, "RECAPTCHA_TOKEN_REQUIRED", secret } } });
        Require(FlowCookieAuthHandler.DecodeBatchResponse(captchaError, "HTrJv").FailureReason == "captcha", "Structured captcha requirement not classified.");
        Require(FlowCookieAuthHandler.DecodeBatchResponse(errorJson, "HTrJv").FailureReason is null, "Generic permission error invented a captcha reason.");
        Require(FlowCookieAuthHandler.DecodeBatchResponse("[[\"wrb.fr\",\"HTrJv\",\"null\"]]", "HTrJv").ErrorCode == 16, "Null login response treated as authenticated.");
        Require(FlowCookieAuthHandler.DecodeBatchResponse("[]", "HTrJv").ErrorCode == 16, "Missing response treated as authenticated.");
        try { FlowCookieAuthHandler.DecodeBatchResponse(")]}'\n999\n[]", "HTrJv"); throw new Exception("Truncated frame accepted."); }
        catch (InvalidDataException) { }

        using var failureAdapter = new FlowCookieAuthHandler(new FixtureHandler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(errorJson) }))) { Session = session };
        using var failureClient = new HttpClient(failureAdapter);
        using var failureRequest = new HttpRequestMessage(HttpMethod.Post, FlowModelCatalogService.Endpoint);
        failureRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FlowCookieAuthHandler.SessionCredentialMarker);
        failureRequest.Content = new StringContent("[]");
        using var failureResponse = await failureClient.SendAsync(failureRequest, cancellationToken);
        var failureBody = await failureResponse.Content.ReadAsStringAsync(cancellationToken);
        Require(failureResponse.StatusCode == HttpStatusCode.Forbidden && !failureBody.Contains(secret) && !failureBody.Contains(xsrf), "Remote error leaked session data.");

        var generationCalls=0;
        using var deniedGenerationClient=new HttpClient(new FixtureHandler((request,token)=>
        {
            generationCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){RequestMessage=request,Content=new StringContent("[7,\"Synthetic permission denial\"]",Encoding.UTF8,"application/json+protobuf")});
        }));
        using var unusedDownloads=new HttpClient(new FixtureHandler((request,token)=>throw new InvalidOperationException("Denied generation attempted a download.")));
        var deniedGeneration=new FlowGenerationService(deniedGenerationClient,unusedDownloads);
        try
        {
            await deniedGeneration.GenerateAsync(new("00000000-0000-0000-0000-000000000001","synthetic-video-model","Synthetic test",Kind:"video"),"synthetic-token",Path.GetTempPath(),cancellationToken:cancellationToken);
            throw new InvalidOperationException("Denied media generation was accepted.");
        }
        catch(FlowGenerationException error)
        {
            Require(error.HttpStatusCode==403&&error.CanonicalCode==7&&generationCalls==1,"Generation lost the structured denial code or repeated the submission.");
        }

        using var foreignRequest = new HttpRequestMessage(HttpMethod.Post, "https://example.com/$rpc/google.internal.labs.aisandbox.proto.flow.v1.FlowService/GetModels");
        foreignRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FlowCookieAuthHandler.SessionCredentialMarker);
        foreignRequest.Content = new StringContent("[]");
        try { using var unexpected = await client.SendAsync(foreignRequest, cancellationToken); throw new Exception("Foreign host accepted."); }
        catch (InvalidOperationException) { }
        adapter.Session = null;
        using var missing = new HttpRequestMessage(HttpMethod.Post, FlowModelCatalogService.Endpoint);
        missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FlowCookieAuthHandler.SessionCredentialMarker);
        missing.Content = new StringContent("[]");
        try { using var unexpected = await client.SendAsync(missing, cancellationToken); throw new Exception("Missing session accepted."); }
        catch (InvalidOperationException) { }
        Require(calls == 1, "Invalid calls reached transport.");
        return "Native BOQ mapping, cookie host/path/expiry limits, UTF-16 framing from public Closure and a live anonymous response, canonical errors, secret redaction and fail-closed authentication passed.";
    }

    static string Frame(string rpcId, string payload)
    {
        var json = JsonSerializer.Serialize(new object?[] { new object?[] { "wrb.fr", rpcId, payload, null, null, null, "generic" } }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var frame = "\n" + json + "\n";
        return ")]}'\n\n" + frame.Length + frame;
    }

    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
}
