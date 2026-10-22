using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FlowStudio.Services;

/// <summary>One real model usage from the account catalog. ModelKey is the generation key.</summary>
public sealed record FlowCatalogModel(string Kind, string ModelId, string DisplayName, string ModelKey,
    int? DurationSeconds = null, IReadOnlyList<string>? AspectRatios = null,
    bool SupportsTextGeneration = true, IReadOnlyDictionary<int, double?>? CreditCosts = null,
    IReadOnlyList<string>? Resolutions = null,
    IReadOnlyList<IReadOnlyList<int>>? InputCombinations = null)
{
    public string FamilyKey => string.IsNullOrEmpty(ModelId) ? DisplayName : ModelId;
    public string VariantLabel => Kind == "video" && DurationSeconds is int seconds
        ? $"{seconds} с" + (Resolutions is {Count:>0} ? " · " + string.Join(" / ", Resolutions) : "") : "Изображение";
    public string SelectionLabel => Kind == "video" && DurationSeconds is not null
        ? $"{DisplayName} · {VariantLabel}" : DisplayName;
    public double? CreditsForTier(int? creditType) => creditType is int tier && CreditCosts is not null &&
        CreditCosts.TryGetValue(tier, out var credits) ? credits : null;
    public bool IsAvailableForTier(int? creditType) => creditType is null || CreditCosts is null || CreditCosts.Count == 0 ||
        CreditCosts.TryGetValue(creditType.Value, out var value) && value is not null;
    public bool SupportsInputs(IEnumerable<int> required)
    {
        if(InputCombinations is null || InputCombinations.Count==0) return true;
        var values=required.Distinct().Order().ToArray();
        return InputCombinations.Any(option=>option.Distinct().Order().SequenceEqual(values));
    }
}

public sealed record FlowModelFamily(string Key, string DisplayName, IReadOnlyList<FlowCatalogModel> Variants);

public sealed class FlowCatalogException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public FlowCatalogException(string message, HttpStatusCode? statusCode = null) : base(message) => StatusCode = statusCode;
}

/// <summary>
/// Read-only catalog through the private Flow RPC. Schema is recovered from Google's
/// public Flow JavaScript, not guessed from REST field names. No generation is submitted.
/// An injected HTTP client must disable automatic redirects and have no default credentials.
/// </summary>
public sealed class FlowModelCatalogService : IDisposable
{
    public static Uri Endpoint { get; } = new("https://aisandbox-pa.googleapis.com/$rpc/google.internal.labs.aisandbox.proto.flow.v1.FlowService/GetModels");
    private const int MaxCatalogBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public FlowModelCatalogService(HttpClient? httpClient = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
        { Timeout = TimeSpan.FromSeconds(30) };
        EnsureNoDefaultCredentials();
    }

    public async Task<IReadOnlyList<FlowCatalogModel>> ListAsync(string bearerToken, CancellationToken cancellationToken = default)
    {
        var token = NormalizeToken(bearerToken);
        EnsureNoDefaultCredentials();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json+protobuf"));
        // Public UI: Q6a.Cb(_.Eu(new t6a)); no client-context fields are set for GetModels.
        request.Content = new StringContent("[]", Encoding.UTF8, "application/json+protobuf");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actual && actual != Endpoint)
                throw new FlowCatalogException("Каталог Flow вернул неожиданное перенаправление.");
            if (!response.IsSuccessStatusCode) throw StatusError(response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaxCatalogBytes)
                throw new FlowCatalogException("Каталог моделей Flow превышает допустимый размер.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(chunk.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxCatalogBytes) throw new FlowCatalogException("Каталог моделей Flow превышает допустимый размер.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
            }
            return ParseCatalog(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FlowCatalogException("Flow не ответил за 30 секунд.");
        }
        catch (HttpRequestException)
        {
            throw new FlowCatalogException("Не удалось подключиться к каталогу Flow. Проверьте интернет и системный прокси.");
        }
        catch (IOException)
        {
            throw new FlowCatalogException("Соединение с Flow прервалось при получении каталога.");
        }
    }

    /// <summary>Parses the JSON+protobuf array format of GetModels, without making network requests.</summary>
    public static IReadOnlyList<FlowCatalogModel> ParseCatalog(string json)
    {
        if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxCatalogBytes)
            throw new FlowCatalogException("Ответ каталога Flow пуст или слишком велик.");
        try
        {
            // Some Google transports prefix executable JSON with the standard anti-XSSI marker.
            var text = json.AsSpan().TrimStart();
            if (text.StartsWith(")]}'".AsSpan(), StringComparison.Ordinal))
            {
                var lineEnd = text.IndexOf('\n');
                if (lineEnd < 0) throw new FlowCatalogException("Flow вернул неполный ответ каталога.");
                text = text[(lineEnd + 1)..].TrimStart();
            }
            using var document = JsonDocument.Parse(text.ToString());
            var root = document.RootElement;
            // Public mapper: nab(_.Y(response,O6a,1)).
            var config = Field(root, 1);
            if (root.ValueKind != JsonValueKind.Array || config.ValueKind != JsonValueKind.Array)
                throw new FlowCatalogException("Формат каталога Flow изменился или Google вернул ошибку. Модели не подставлены автоматически.");
            var result = new List<FlowCatalogModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Public nab: image repeated M6a field 6; video repeated M6a field 5.
            ReadModels(Field(config, 6), "image", result, seen);
            ReadModels(Field(config, 5), "video", result, seen);
            return result;
        }
        catch (JsonException)
        {
            throw new FlowCatalogException("Flow вернул некорректный JSON-каталог моделей.");
        }
    }

    private static void ReadModels(JsonElement models, string kind, List<FlowCatalogModel> result, HashSet<string> seen)
    {
        if (models.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
        if (models.ValueKind != JsonValueKind.Array) throw new FlowCatalogException("Структура списка моделей Flow изменилась.");
        foreach (var model in models.EnumerateArray())
        {
            // M6a.re(): string field 1; getKey(): string field 4; usages: repeated L6a field 2.
            var displayName = Text(Field(model, 1));
            var modelId = Text(Field(model, 4));
            // A1a() excludes family.At (M6a field 6) and usage.At (L6a field 25).
            if (Boolean(Field(model, 6))) continue;
            var usages = Field(model, 2);
            if (usages.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            if (model.ValueKind != JsonValueKind.Array || usages.ValueKind != JsonValueKind.Array)
                throw new FlowCatalogException("Структура модели Flow изменилась.");
            foreach (var usage in usages.EnumerateArray())
            {
                // L6a.getKey(): string field 1. This is distinct from the parent model ID.
                var key = Text(Field(usage, 1));
                if (string.IsNullOrEmpty(key) || Boolean(Field(usage, 25))) continue;
                var seconds = kind == "video" ? Integer(Field(usage, 17)) ?? 8 : (int?)null;
                if(seconds is < 1 or > 600) throw new FlowCatalogException("Flow вернул неподдерживаемую длительность модели.");
                var aspects = ParseAspects(Field(usage, kind == "video" ? 13 : 14), kind);
                var pureText = SupportsPlainText(Field(usage, kind == "video" ? 8 : 9));
                var inputs = ParseInputs(Field(usage,kind=="video"?8:9));
                var costs = ParseCosts(Field(usage, 5));
                var resolutions = kind == "video" ? ParseResolutions(Field(usage,24)) : Array.Empty<string>();
                if (seen.Add(kind + "\0" + key)) result.Add(new FlowCatalogModel(kind, modelId ?? "", displayName ?? modelId ?? key,
                    key, seconds, aspects, pureText, costs,resolutions,inputs));
                if (result.Count > 2000) throw new FlowCatalogException("Каталог Flow содержит слишком много моделей.");
            }
        }
    }

    public static IReadOnlyList<FlowCatalogModel> GetTextModels(IEnumerable<FlowCatalogModel> models,
        string kind, string? aspectRatio = null, int? creditType = null) => models
        .Where(model => model.Kind == kind && model.SupportsTextGeneration && model.IsAvailableForTier(creditType) &&
            (string.IsNullOrEmpty(aspectRatio) || model.AspectRatios is null || model.AspectRatios.Count == 0 || model.AspectRatios.Contains(aspectRatio)))
        .GroupBy(model => model.ModelKey, StringComparer.Ordinal).Select(group => group.First())
        .OrderBy(model => model.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(model => model.DurationSeconds).ThenBy(model => model.ModelKey, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<FlowModelFamily> GetFamilies(IEnumerable<FlowCatalogModel> models,
        string kind, string? aspectRatio = null, int? creditType = null) => GetTextModels(models, kind, aspectRatio, creditType)
        .GroupBy(model => model.FamilyKey, StringComparer.Ordinal)
        .Select(group => new FlowModelFamily(group.Key, group.First().DisplayName, group.ToArray())).ToArray();

    /// <summary>Exact source input sets: text[1], image references[1,3], base image[1,4],
    /// start frame[1,5], start+end[1,5,7], video image-ingredients[1,6].</summary>
    public static IReadOnlyList<FlowCatalogModel> GetInputModels(IEnumerable<FlowCatalogModel> models,string kind,
        IReadOnlyCollection<int> inputs,string? aspectRatio=null,int? modelUserTier=null) => models
        .Where(model=>model.Kind==kind && model.SupportsInputs(inputs) && model.IsAvailableForTier(modelUserTier) &&
            (string.IsNullOrEmpty(aspectRatio)||model.AspectRatios is null||model.AspectRatios.Count==0||model.AspectRatios.Contains(aspectRatio)))
        .GroupBy(model=>model.ModelKey,StringComparer.Ordinal).Select(group=>group.First())
        .OrderBy(model=>model.DisplayName,StringComparer.CurrentCultureIgnoreCase).ThenBy(model=>model.DurationSeconds)
        .ThenBy(model=>model.ModelKey,StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<FlowModelFamily> GetInputFamilies(IEnumerable<FlowCatalogModel> models,string kind,
        IReadOnlyCollection<int> inputs,string? aspectRatio=null,int? modelUserTier=null) => GetInputModels(models,kind,inputs,aspectRatio,modelUserTier)
        .GroupBy(model=>model.FamilyKey,StringComparer.Ordinal)
        .Select(group=>new FlowModelFamily(group.Key,group.First().DisplayName,group.ToArray())).ToArray();

    static bool Boolean(JsonElement field) => field.ValueKind == JsonValueKind.True ||
        field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var value) && value == 1;
    static int? Integer(JsonElement field) => field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var value) ? value : null;

    static IReadOnlyList<string> ParseAspects(JsonElement message, string kind)
    {
        var numbers=Field(message,1);
        if(numbers.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return Array.Empty<string>();
        if(numbers.ValueKind!=JsonValueKind.Array) throw new FlowCatalogException("Структура форматов модели Flow изменилась.");
        return numbers.EnumerateArray().Select(value => (kind,Integer(value)) switch
        {
            ("video",1)=>"9:16", ("video",2)=>"16:9",
            ("image",1)=>"1:1", ("image",2)=>"9:16", ("image",3)=>"16:9", ("image",4)=>"3:4", ("image",5)=>"4:3", _=>""
        }).Where(value=>value.Length>0).Distinct().ToArray();
    }

    static bool SupportsPlainText(JsonElement message)
    {
        // The website passes mP=nP=[1] for text-only input and bO compares the complete set.
        var options=Field(message,1);
        if(options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        if(options.ValueKind!=JsonValueKind.Array) throw new FlowCatalogException("Структура входов модели Flow изменилась.");
        if(options.GetArrayLength()==0) return true;
        foreach(var option in options.EnumerateArray())
        {
            var values=Field(option,1);
            if(values.ValueKind==JsonValueKind.Array && values.GetArrayLength()==1 && Integer(values[0])==1) return true;
        }
        return false;
    }

    static IReadOnlyList<IReadOnlyList<int>> ParseInputs(JsonElement message)
    {
        var options=Field(message,1);
        if(options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return Array.Empty<IReadOnlyList<int>>();
        if(options.ValueKind!=JsonValueKind.Array) throw new FlowCatalogException("Структура входов модели Flow изменилась.");
        var result=new List<IReadOnlyList<int>>();
        foreach(var option in options.EnumerateArray())
        {
            var values=Field(option,1);
            if(values.ValueKind!=JsonValueKind.Array) continue;
            var ids=values.EnumerateArray().Select(value=>Integer(value)??0).Where(value=>value>0).Distinct().ToArray();
            if(ids.Length>0) result.Add(ids);
        }
        return result;
    }

    static IReadOnlyList<string> ParseResolutions(JsonElement message)
    {
        // kab() reads F6a.field1 through B0a; hO() uses [1] (720p) when absent.
        var values=Field(message,1);
        if(values.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new[]{"720p"};
        if(values.ValueKind!=JsonValueKind.Array) throw new FlowCatalogException("Структура разрешений модели Flow изменилась.");
        var result=values.EnumerateArray().Select(value=>Integer(value) switch{4=>"360p",1=>"720p",2=>"1080p",3=>"4K",_=>""})
            .Where(value=>value.Length>0).Distinct().ToArray();
        return result.Length==0?new[]{"720p"}:result;
    }

    static IReadOnlyDictionary<int,double?> ParseCosts(JsonElement entries)
    {
        var costs=new Dictionary<int,double?>();
        if(entries.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return costs;
        if(entries.ValueKind!=JsonValueKind.Array) throw new FlowCatalogException("Структура стоимости модели Flow изменилась.");
        foreach(var entry in entries.EnumerateArray())
        {
            var tier=Integer(Field(entry,1));
            if(tier is null or < 1 or > 100) continue;
            var state=Field(entry,2);
            var price=Field(Field(state,1),2);
            double? value=null;
            // wab(): missing value or oneof field2 means unavailable. Field1.w6a.field2 is credit cost.
            if(state.ValueKind==JsonValueKind.Array && Field(state,2).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if(price.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) value=0; // wab() defaults missing scalar to zero.
                else if(price.ValueKind==JsonValueKind.Number && price.TryGetDouble(out var number) && double.IsFinite(number) && number>=0) value=number;
            }
            costs[tier.Value]=value;
        }
        return costs;
    }

    private static JsonElement Field(JsonElement message, int number) =>
        message.ValueKind == JsonValueKind.Array && message.GetArrayLength() >= number ? message[number - 1] : default;

    private static string? Text(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FlowCatalogException("Каталог Flow содержит неожиданный тип поля.");
        var text = value.GetString();
        if (text is not null && (text.Length > 512 || text.AsSpan().ContainsAny('\r', '\n', '\0')))
            throw new FlowCatalogException("Каталог Flow содержит некорректное имя модели.");
        return text;
    }

    private void EnsureNoDefaultCredentials()
    {
        if (_http.DefaultRequestHeaders.Authorization is not null || _http.DefaultRequestHeaders.Contains("x-goog-api-key") || _http.DefaultRequestHeaders.Contains("Cookie"))
            throw new ArgumentException("Для Flow используйте отдельный HttpClient без общих токенов, API-ключей и cookies.");
    }

    private static string NormalizeToken(string value)
    {
        var token = (value ?? "").Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
        if (token.Length is < 10 or > 16384 || !Regex.IsMatch(token, @"\A[A-Za-z0-9._~+/-]+=*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Введите корректный OAuth access token аккаунта Flow. Gemini API-ключ здесь не подходит.", nameof(value));
        return token;
    }

    private static FlowCatalogException StatusError(HttpStatusCode status) => new((int)status switch
    {
        401 => "Google отклонил OAuth-токен Flow: он истёк или не имеет нужной авторизации.",
        403 => "Нет доступа к каталогу Flow. Проверьте аккаунт, авторизацию и регион.",
        429 => "Достигнут лимит запросов Flow. Повторите проверку позднее.",
        404 => "Google изменил маршрут каталога Flow. Необходима проверка протокола.",
        _ => $"Не удалось получить каталог Flow (HTTP {(int)status})."
    }, status);

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
