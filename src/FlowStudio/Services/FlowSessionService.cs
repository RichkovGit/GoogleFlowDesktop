using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlowStudio.Services;

public enum FlowSessionStatus
{
    MissingToken,
    Reachable,
    Unauthorized,
    Forbidden,
    Unavailable,
    InvalidResponse
}

public sealed record FlowSessionProbeResult(
    FlowSessionStatus Status,
    int? HttpStatusCode,
    string Message,
    int? ModelCount = null);

/// <summary>
/// Read-only probe for the private Flow service. No generation or account mutation.
/// The route and RPC method are confirmed by the supplied APK and an unauthenticated
/// response from Google's live service; see research/protocol.md.
/// </summary>
public sealed class FlowSessionService(HttpClient httpClient)
{
    public static Uri ModelsEndpoint { get; } = new("https://aisandbox-pa.googleapis.com/v1/flow/models");

    public const string NativeLoginExplanation =
        "Вход в аккаунт Flow без браузера в этом клиенте не реализован. " +
        "Android SSO и reCAPTCHA из APK нельзя перенести в Windows. " +
        "Проверка ниже принимает уже полученный OAuth access token; каталог моделей не подтверждает доступ к генерации.";

    public async Task<FlowSessionProbeResult> ProbeAsync(string? bearerToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return new(FlowSessionStatus.MissingToken, null, "Для проверки Flow нужен OAuth access token. Gemini API key здесь не подходит.");

        var token = bearerToken.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();

        if (string.IsNullOrEmpty(token) || token.Length > 16384 ||
            token.Any(c => !char.IsAsciiLetterOrDigit(c) && "-._~+/=".IndexOf(c) < 0))
            return new(FlowSessionStatus.Unauthorized, null, "Неверный формат OAuth access token.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(FlowSessionStatus.Unauthorized, code, "Google отклонил токен: он истёк или не имеет нужной авторизации Flow.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new(FlowSessionStatus.Forbidden, code, "Google запретил доступ к Flow. Регион, аккаунт, scope или клиент могут не поддерживаться.");
            if (!response.IsSuccessStatusCode)
                return new(FlowSessionStatus.Unavailable, code, $"Flow вернул HTTP {code}. Проверка не подтверждена.");

            // Never store or expose the service body; it may contain account-specific data.
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > 4 * 1024 * 1024)
                    return new(FlowSessionStatus.InvalidResponse, code, "Ответ каталога Flow превышает допустимый размер.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token);
            }

            using var json = JsonDocument.Parse(buffer.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.TryGetProperty("error", out _))
                return new(FlowSessionStatus.InvalidResponse, code, "Flow вернул неожиданный формат ответа.");

            int? count = json.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                ? models.GetArrayLength()
                : null;
            return new(FlowSessionStatus.Reachable, code,
                "Каталог Flow доступен. Доступ к генерации и расходованию Flow credits этой проверкой не подтверждён.", count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(FlowSessionStatus.Unavailable, null, "Flow не ответил за 20 секунд.");
        }
        catch (HttpRequestException)
        {
            return new(FlowSessionStatus.Unavailable, null, "Не удалось соединиться с Flow. Проверьте подключение и настройки сети.");
        }
        catch (IOException)
        {
            return new(FlowSessionStatus.Unavailable, null, "Соединение с Flow прервалось при получении ответа.");
        }
        catch (JsonException)
        {
            return new(FlowSessionStatus.InvalidResponse, null, "Flow вернул ответ, который не удалось прочитать как JSON.");
        }
    }
}
