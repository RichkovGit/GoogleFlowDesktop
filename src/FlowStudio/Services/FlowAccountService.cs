using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlowStudio.Services;

/// <summary>
/// Account values returned by Google's GetCredits RPC. Missing fields stay unknown;
/// enum values are preserved without guessing labels or generation eligibility.
/// </summary>
public sealed record FlowCreditBalance(
    long? TotalCredits,
    long? AiCredits,
    long? FlowCredits,
    int? CreditType,
    int? SubscriptionTier,
    int? ModelUserTier,
    DateTimeOffset? NextDailyRefresh);

public sealed class FlowAccountException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// Credit-free account read through the same native RPC transport as generation.
/// The injected client supplies the user-authorized session adapter when applicable.
/// Source: public_flow_1.js GetCredits (nzlxg, eza / hz) and EI credit-panel mapping.
/// </summary>
public sealed class FlowAccountService(HttpClient client)
{
    public static Uri CreditsEndpoint { get; } = new(
        "https://aisandbox-pa.googleapis.com/$rpc/google.internal.labs.aisandbox.proto.videofx.v1.VideoFxService/GetCredits");
    const int MaxResponseBytes = 64 * 1024;

    public async Task<FlowCreditBalance> GetCreditsAsync(string? credential = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, CreditsEndpoint);
        if (!string.IsNullOrWhiteSpace(credential))
        {
            var token = credential.Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
            if (token.Length > 16384 || token.Length == 0 ||
                Array.Exists(token.ToCharArray(), c => !char.IsAsciiLetterOrDigit(c) && "-._~+/=".IndexOf(c) < 0))
                throw new ArgumentException("Неверный формат авторизации Flow.", nameof(credential));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json+protobuf"));
        // Public credit-panel query: gza.Cb(new eza), no fields set.
        request.Content = new StringContent("[]", Encoding.UTF8, "application/json+protobuf");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw StatusError(response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new FlowAccountException("Ответ баланса Flow превышает допустимый размер.");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (data.Length + read > MaxResponseBytes)
                    throw new FlowAccountException("Ответ баланса Flow превышает допустимый размер.");
                await data.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            return ParseCreditsResponse(Encoding.UTF8.GetString(data.GetBuffer(), 0, checked((int)data.Length)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FlowAccountException("Flow не ответил на запрос баланса за 30 секунд.");
        }
        catch (HttpRequestException)
        {
            throw new FlowAccountException("Не удалось подключиться к Flow для проверки баланса.");
        }
        catch (IOException)
        {
            throw new FlowAccountException("Соединение с Flow прервалось при проверке баланса.");
        }
    }

    public static FlowCreditBalance ParseCreditsResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
            throw new FlowAccountException("Ответ баланса Flow пуст или слишком велик.");
        try
        {
            var text = json.AsSpan().TrimStart();
            if (text.StartsWith(")]}'".AsSpan(), StringComparison.Ordinal))
            {
                var end = text.IndexOf('\n');
                if (end < 0) throw new FlowAccountException("Flow вернул неполный ответ баланса.");
                text = text[(end + 1)..].TrimStart();
            }
            using var document = JsonDocument.Parse(text.ToString());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || Field(root, 2).ValueKind == JsonValueKind.String)
                throw new FlowAccountException("Google не подтвердил баланс Flow. Требуется повторная проверка аккаунта.");
            return new(
                Number(root, 1), Number(root, 5), Number(root, 6),
                Enum(root, 2), Enum(root, 3), Enum(root, 4), Timestamp(Field(root, 7)));
        }
        catch (JsonException)
        {
            throw new FlowAccountException("Flow вернул некорректный JSON баланса.");
        }
    }

    static JsonElement Field(JsonElement message, int number) =>
        message.ValueKind == JsonValueKind.Array && message.GetArrayLength() >= number ? message[number - 1] : default;

    static long? Number(JsonElement message, int number)
    {
        var value = Field(message, number);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        // jspb uses a decimal string for some int64 values.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) && integer >= 0) return integer;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out integer) && integer >= 0) return integer;
        throw new FlowAccountException("Структура баланса Flow изменилась: ожидалось целое неотрицательное число.");
    }

    static int? Enum(JsonElement message, int number)
    {
        var value = Number(message, number);
        if (value > int.MaxValue) throw new FlowAccountException("Структура типа аккаунта Flow изменилась.");
        return value is null ? null : (int)value;
    }

    static DateTimeOffset? Timestamp(JsonElement message)
    {
        if (message.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (message.ValueKind != JsonValueKind.Array) throw new FlowAccountException("Flow вернул некорректное время обновления баланса.");
        var seconds = Number(message, 1);
        var nanos = Number(message, 2) ?? 0;
        if (seconds is null) return null;
        if (nanos >= 1_000_000_000) throw new FlowAccountException("Flow вернул некорректное время обновления баланса.");
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).AddTicks(nanos / 100); }
        catch (ArgumentOutOfRangeException) { throw new FlowAccountException("Flow вернул некорректное время обновления баланса."); }
    }

    static FlowAccountException StatusError(HttpStatusCode statusCode) => new((int)statusCode switch
    {
        401 => "Сессия Flow истекла. Войдите в Google повторно.",
        403 => "Google запретил чтение баланса Flow. Проверьте доступ аккаунта и регион.",
        429 => "Flow временно ограничил запросы баланса. Повторите позже.",
        _ => $"Flow не подтвердил баланс (HTTP {(int)statusCode})."
    }, statusCode);
}
