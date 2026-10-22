using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlowStudio.Services;

/// <summary>Credential-free account checks; no Google requests or user storage access.</summary>
public static class FlowAccountSelfTest
{
    public static async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var balance = FlowAccountService.ParseCreditsResponse("[130,2,3,2,100,30,[1792500000,123000000]]");
        Require(balance.TotalCredits == 130 && balance.AiCredits == 100 && balance.FlowCredits == 30,
            "Separate credit pools were merged or mapped to the wrong balance.");
        Require(balance.CreditType == 2 && balance.SubscriptionTier == 3 && balance.ModelUserTier == 2,
            "Account enum values were changed.");
        Require(balance.NextDailyRefresh == DateTimeOffset.FromUnixTimeSeconds(1792500000).AddMilliseconds(123),
            "The daily credit refresh timestamp was converted incorrectly.");

        var missing = FlowAccountService.ParseCreditsResponse("[]");
        Require(missing.TotalCredits is null && missing.AiCredits is null && missing.FlowCredits is null && missing.NextDailyRefresh is null,
            "Missing credit values were displayed as a confirmed zero balance.");
        var zero = FlowAccountService.ParseCreditsResponse("[0,null,null,null,0,0]");
        Require(zero.TotalCredits == 0 && zero.AiCredits == 0 && zero.FlowCredits == 0,
            "A server-confirmed zero balance became unknown.");
        Require(FlowAccountService.ParseCreditsResponse(")]}'\n[\"500\"]").TotalCredits == 500,
            "An int64/XSSI balance could not be decoded.");

        foreach (var invalid in new[]
        {
            "[16,\"Credential expired\"]", "{\"error\":{\"code\":401}}", "[-1]", "[1.25]",
            "[1,null,null,null,null,null,[123,1000000000]]"
        })
        {
            var rejected = false;
            try { FlowAccountService.ParseCreditsResponse(invalid); }
            catch (FlowAccountException) { rejected = true; }
            Require(rejected, "An authentication error or malformed value was accepted as an account balance.");
        }

        var sent = 0;
        using (var client = new HttpClient(new AccountHandler(async (request, token) =>
        {
            sent++;
            Require(request.Method == HttpMethod.Post && request.RequestUri == FlowAccountService.CreditsEndpoint,
                "The account read used an unexpected endpoint.");
            Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "flow-cookie-session",
                "The native login-session marker was not passed to the auth adapter.");
            Require(request.Content?.Headers.ContentType?.MediaType == "application/json+protobuf" &&
                await request.Content.ReadAsStringAsync(token).ConfigureAwait(false) == "[]",
                "The credit read must send the source-confirmed empty GetCredits request.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[7]", Encoding.UTF8, "application/json+protobuf") };
        })))
        {
            var result = await new FlowAccountService(client).GetCreditsAsync("flow-cookie-session", cancellationToken).ConfigureAwait(false);
            Require(sent == 1 && result.TotalCredits == 7, "The read-only account request failed or was repeated.");
        }

        const string secretInRejectedBody = "synthetic-rejected-session-value";
        using (var client = new HttpClient(new AccountHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent(secretInRejectedBody) }))))
        {
            var rejected = false;
            try { await new FlowAccountService(client).GetCreditsAsync("flow-cookie-session", cancellationToken).ConfigureAwait(false); }
            catch (FlowAccountException exception)
            {
                rejected = true;
                Require(exception.StatusCode == HttpStatusCode.Unauthorized && !exception.Message.Contains(secretInRejectedBody, StringComparison.Ordinal),
                    "An expired session must surface HTTP 401 without exposing a response body.");
            }
            Require(rejected, "An expired session was treated as an authenticated account.");
        }

        return "Distinct/zero/unknown credit pools, enum values, refresh timestamp, XSSI/int64, rejected error/malformed responses, exact read-only RPC, and HTTP 401 verified. No real network or credit spending.";
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    sealed class AccountHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
