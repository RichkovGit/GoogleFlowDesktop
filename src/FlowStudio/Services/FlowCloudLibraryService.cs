using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

public sealed record FlowCloudProjectInfo(string Id, string Title, DateTimeOffset? UpdatedAt, string? ThumbnailUrl);
public sealed record FlowCloudMediaInfo(string MediaName, string ProjectId, string WorkflowId, string Title,
    string Prompt, string Kind, string ModelKey, DateTimeOffset? CreatedAt, string? ThumbnailUrl, string Status,
    bool IsFavorite = false, string CollectionId = "", bool IsTrashed = false);

public sealed class FlowCloudLibraryException(string message, int? httpStatusCode = null, int? canonicalCode = null, string? failureReason = null) : Exception(message)
{
    public int? HttpStatusCode { get; } = httpStatusCode;
    public int? CanonicalCode { get; } = canonicalCode;
    public string? FailureReason { get; } = failureReason;
}

/// <summary>Native account project and media reads using the same confirmed RPCs as the Flow web client.</summary>
public sealed class FlowCloudLibraryService(HttpClient api, HttpClient downloads)
{
    const string Service = "google.internal.labs.aisandbox.proto.flow.v1.FlowService";

    public async Task<IReadOnlyList<FlowCloudProjectInfo>> ListProjectsAsync(string credential, CancellationToken ct = default)
    {
        var result = new List<FlowCloudProjectInfo>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        for (var page = 0; page < 500; page++)
        {
            // IM.Wb: hXa(21), iXa(nextPage), sort3. Response xM repeated1 / token2.
            var response = await FlowNativeRpc.SendAsync(api, Service, "GetProjects", new object?[] { "projects/*", 21, token, 3 }, credential, ct);
            foreach (var item in FlowNativeRpc.Repeated(response, 1))
            {
                var rawId = FlowNativeRpc.Text(item, 1);
                if (!TryProjectId(rawId, out var id) || !ids.Add(id)) continue;
                var metadata = FlowNativeRpc.Field(item, 2);
                result.Add(new(id, FlowNativeRpc.Text(metadata, 1) ?? "Без названия", FlowNativeRpc.Timestamp(FlowNativeRpc.Field(metadata, 3)),
                    AllowedUrl(FlowNativeRpc.Text(metadata, 4))));
                if (result.Count > 10000) throw new FlowCloudLibraryException("Список проектов Flow превышает предел 10 000.");
            }
            token = FlowNativeRpc.Text(response, 2);
            if (string.IsNullOrEmpty(token)) return result;
            if (!tokens.Add(token)) throw new FlowCloudLibraryException("Flow вернул повторяющуюся страницу проектов.");
        }
        throw new FlowCloudLibraryException("Flow вернул слишком много страниц проектов.");
    }

    public async Task<IReadOnlyList<FlowCloudMediaInfo>> ListMediaAsync(string projectId, string credential, CancellationToken ct = default)
    {
        projectId = NormalizeProjectId(projectId);
        // obb() -> S6a(`projects/${id}`), no pagination in GetProjectContents.
        var response = await FlowNativeRpc.SendAsync(api, Service, "GetProjectContents", new object?[] { "projects/" + projectId }, credential, ct);
        return ParseMedia(response, projectId);
    }

    internal static IReadOnlyList<FlowCloudMediaInfo> ParseMedia(JsonElement response, string projectId, bool includeTrashed = false)
    {
        var workflows = FlowNativeRpc.Repeated(response, 2)
            .Where(w => !string.IsNullOrEmpty(FlowNativeRpc.Text(w, 1)))
            .GroupBy(w => FlowNativeRpc.Text(w, 1)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = new List<FlowCloudMediaInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var media in FlowNativeRpc.Repeated(response, 3))
        {
            var name = FlowNativeRpc.Text(media, 1);
            if (string.IsNullOrEmpty(name) || name.Length > 2048 || name.Any(char.IsControl) || !seen.Add(name)) continue;
            var image = FlowNativeRpc.Field(media, 7);
            var video = FlowNativeRpc.Field(media, 8);
            var kind = image.ValueKind == JsonValueKind.Array ? "image" : video.ValueKind == JsonValueKind.Array ? "video" : null;
            if (kind is null) continue; // Audio and likeness are not presented as video/image.
            var workflowId = FlowNativeRpc.Text(media, 3) ?? "";
            workflows.TryGetValue(workflowId, out var workflow);
            var workflowMetadata = FlowNativeRpc.Field(workflow, 4);
            // Py metadata.archived3 marks a trashed workflow; don't resurrect it in the library.
            var isTrashed = FlowNativeRpc.Boolean(workflowMetadata, 3);
            if (isTrashed && !includeTrashed) continue;
            var metadata = FlowNativeRpc.Field(media, 6);
            var generated = FlowNativeRpc.Field(kind == "image" ? image : video, 1);
            var prompt = FlowNativeRpc.Text(generated, 8) ?? "";
            var structured = FlowNativeRpc.Field(metadata, 7);
            if (structured.ValueKind != JsonValueKind.Array) structured = FlowNativeRpc.Field(generated, kind == "image" ? 16 : 18);
            var promptParts = FlowNativeRpc.Repeated(FlowNativeRpc.Field(FlowNativeRpc.Repeated(structured, 3).FirstOrDefault(), 3), 1)
                .Select(part => FlowNativeRpc.Text(part, 1)).Where(text => !string.IsNullOrWhiteSpace(text));
            var reconstructed = string.Join("", promptParts);
            if (reconstructed.Length > 0) prompt = reconstructed;
            var title = FlowNativeRpc.Text(workflowMetadata, 1);
            if (string.IsNullOrWhiteSpace(title)) title = string.IsNullOrWhiteSpace(prompt) ? kind == "video" ? "Видео Flow" : "Изображение Flow" : prompt.Length > 100 ? prompt[..100] + "…" : prompt;
            var statusCode = FlowNativeRpc.Integer(FlowNativeRpc.Field(metadata, 9), 1);
            // Public nQ defaults a present image/video subtype to success when
            // metadata.status is absent; URLs are often omitted from this read.
            var status = statusCode switch { 0 or 1 or 2 => "pending", 3 => "success", 4 or 7 => "failed", 5 => "canceled", 6 => "scheduled", _ => "success" };
            // OK() confirms image generated1.url14 / uploaded2.url4; video1.url9.
            // Use actual image URL as optional preview; no fabricated video thumbnail URL.
            var thumbnail = kind == "image" ? AllowedUrl(FlowGenerationService.MediaUrl(media)) : AllowedUrl(FlowNativeRpc.Text(metadata, 11));
            var model = kind == "image" ? FlowNativeRpc.Integer(generated, 9)?.ToString(CultureInfo.InvariantCulture) ?? "" : FlowNativeRpc.Text(generated, 13) ?? "";
            var created = FlowNativeRpc.Timestamp(FlowNativeRpc.Field(metadata, 1)) ?? FlowNativeRpc.Timestamp(FlowNativeRpc.Field(workflowMetadata, 2));
            result.Add(new(name, projectId, workflowId, title!, prompt, kind, model, created, thumbnail, status,
                FlowNativeRpc.Boolean(workflowMetadata, 4), FlowNativeRpc.Text(workflow, 2) ?? "", isTrashed));
            if (result.Count > 50000) throw new FlowCloudLibraryException("Проект Flow содержит слишком много материалов.");
        }
        return result.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<string> DownloadMediaAsync(string mediaName, string projectId, string kind, string credential, string outputDirectory, CancellationToken ct = default)
    {
        _ = NormalizeProjectId(projectId);
        if (kind is not ("image" or "video") || string.IsNullOrWhiteSpace(mediaName) || mediaName.Length > 2048 || mediaName.Any(char.IsControl))
            throw new ArgumentException("Некорректный материал Flow.");
        // Refresh signed media URL instead of persisting it in a project file.
        var media = await FlowNativeRpc.SendAsync(api, Service, "GetMedia", new object?[] { mediaName }, credential, ct);
        if (FlowNativeRpc.Text(media, 1) != mediaName || FlowNativeRpc.Text(media, 2)?.TrimStart('/') is { Length: > 0 } owner &&
            owner != projectId && owner != "projects/" + projectId)
            throw new FlowCloudLibraryException("Flow вернул материал другого проекта.");
        var url = AllowedUrl(FlowGenerationService.MediaUrl(media)) ?? throw new FlowCloudLibraryException("Материал ещё не готов или ссылка недоступна. Обновите медиатеку.");
        return await DownloadAsync(url, mediaName, kind, outputDirectory, 1024L * 1024 * 1024, ct);
    }

    public Task<string> DownloadThumbnailAsync(FlowCloudMediaInfo media, string outputDirectory, CancellationToken ct = default)
    {
        var url = AllowedUrl(media.ThumbnailUrl) ?? throw new FlowCloudLibraryException("У материала нет изображения предпросмотра.");
        return DownloadAsync(url, media.MediaName + "-preview", "image", outputDirectory, 8L * 1024 * 1024, ct);
    }

    public async Task<string> DownloadThumbnailAsync(FlowCloudMediaInfo media, string credential, string outputDirectory, CancellationToken ct = default)
    {
        var url = AllowedUrl(media.ThumbnailUrl);
        if (url is null)
        {
            var refreshed = await FlowNativeRpc.SendAsync(api, Service, "GetMedia", new object?[] { media.MediaName }, credential, ct);
            if (FlowNativeRpc.Text(refreshed, 1) != media.MediaName) throw new FlowCloudLibraryException("Flow вернул другой материал предпросмотра.");
            url = media.Kind == "video" ? AllowedUrl(FlowNativeRpc.Text(FlowNativeRpc.Field(refreshed, 6), 11)) : AllowedUrl(FlowGenerationService.MediaUrl(refreshed));
        }
        if (url is null) throw new FlowCloudLibraryException("У материала нет изображения предпросмотра.");
        return await DownloadAsync(url, media.MediaName + "-preview", "image", outputDirectory, 8L * 1024 * 1024, ct);
    }

    async Task<string> DownloadAsync(string url, string mediaName, string kind, string outputDirectory, long maximumBytes, CancellationToken ct)
    {
        if (downloads.DefaultRequestHeaders.Authorization is not null || downloads.DefaultRequestHeaders.Contains("Cookie") || downloads.DefaultRequestHeaders.Contains("x-goog-api-key"))
            throw new InvalidOperationException("Для загрузки медиа нужен отдельный клиент без авторизации.");
        var uri = new Uri(url);
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mediaName))).ToLowerInvariant();
        Directory.CreateDirectory(outputDirectory);
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (!FlowGenerationService.IsAllowedMediaUri(uri)) throw new FlowCloudLibraryException("Недопустимый сервер медиа Flow.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is { } actual && !FlowGenerationService.IsAllowedMediaUri(actual))
                throw new FlowCloudLibraryException("Загрузка медиа перенаправлена за пределы серверов Google.");
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (response.Headers.Location is null || !Uri.TryCreate(uri, response.Headers.Location, out var next)) throw new FlowCloudLibraryException("Некорректная ссылка медиа Flow.");
                uri = next; continue;
            }
            if (!response.IsSuccessStatusCode) throw new FlowCloudLibraryException($"Не удалось скачать материал Flow (HTTP {(int)response.StatusCode}).", (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > maximumBytes) throw new FlowCloudLibraryException("Материал превышает допустимый размер загрузки.");
            var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            var extension = mime switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "video/mp4" => ".mp4", "video/webm" => ".webm", "application/octet-stream" => ".bin", _ => null };
            if (extension is null || mime != "application/octet-stream" && !mime!.StartsWith(kind + "/", StringComparison.Ordinal)) throw new FlowCloudLibraryException("Сервер вернул неподдерживаемый формат медиа.");
            var final = Path.Combine(Path.GetFullPath(outputDirectory), stem + extension);
            var temporary = final + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; long total = 0; int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        total += read;
                        if (total > maximumBytes) throw new FlowCloudLibraryException("Материал превышает допустимый размер загрузки.");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    if (total == 0) throw new FlowCloudLibraryException("Google вернул пустой файл.");
                }
                if (extension == ".bin")
                {
                    var header = new byte[16]; int length;
                    await using (var stream = File.OpenRead(temporary)) length = await stream.ReadAsync(header, ct);
                    extension = DetectExtension(header, length, kind) ?? throw new FlowCloudLibraryException("Файл Google не распознан как медиа.");
                    final = Path.ChangeExtension(final, extension);
                }
                File.Move(temporary, final, true);
                return final;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        throw new FlowCloudLibraryException("Слишком много перенаправлений медиа Flow.");
    }

    static string? DetectExtension(byte[] header, int count, string kind) => kind == "video"
        ? count >= 12 && Encoding.ASCII.GetString(header, 4, 4) == "ftyp" ? ".mp4" : count >= 4 && header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }) ? ".webm" : null
        : count >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? ".png" : count >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255 ? ".jpg" : count >= 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP" ? ".webp" : null;

    static string? AllowedUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && FlowGenerationService.IsAllowedMediaUri(uri) ? value : null;
    internal static string NormalizeProjectId(string value) => TryProjectId(value, out var id) ? id : throw new ArgumentException("Некорректный UUID проекта Flow.");
    static bool TryProjectId(string? value, out string id)
    {
        value = value?.StartsWith("projects/", StringComparison.Ordinal) == true ? value[9..] : value;
        id = Guid.TryParse(value, out var uuid) ? uuid.ToString() : "";
        return id.Length > 0;
    }
}

internal static class FlowNativeRpc
{
    internal static async Task<JsonElement> SendAsync(HttpClient api, string service, string method, object?[] body, string credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://aisandbox-pa.googleapis.com/$rpc/{service}/{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json+protobuf"));
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json+protobuf");
        using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Content.Headers.ContentLength > 32 * 1024 * 1024) throw new FlowCloudLibraryException("Ответ Flow превышает 32 МБ.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[16384]; int read;
        while ((read = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > 32 * 1024 * 1024) throw new FlowCloudLibraryException("Ответ Flow превышает 32 МБ.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), new JsonDocumentOptions { MaxDepth = 96 });
            var code=Integer(json.RootElement,1);
            var isCanonicalError=code is >=0 and <=16&&Text(json.RootElement,2) is not null;
            if(!response.IsSuccessStatusCode||isCanonicalError)
            {
                var status=!response.IsSuccessStatusCode?(int)response.StatusCode:code switch{3=>400,7=>403,8=>429,16=>401,_=>502};
                throw Error(status,isCanonicalError?code:null,FlowCookieAuthHandler.ClassifyFailure(json.RootElement));
            }
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new FlowCloudLibraryException("Google изменил формат ответа Flow.");
            return json.RootElement.Clone();
        }
        catch (JsonException) { if(!response.IsSuccessStatusCode)throw Error((int)response.StatusCode);throw new FlowCloudLibraryException("Не удалось прочитать ответ Flow."); }
    }
    static FlowCloudLibraryException Error(int status,int? code=null,string? reason=null)=>new(status switch{401=>"Вход Google истёк. Подключите аккаунт заново.",403=>"Google не разрешил доступ к этому разделу Flow.",429=>"Лимит запросов Flow. Повторите позднее.",_=>$"Flow вернул HTTP {status}."},status,code,reason);
    internal static JsonElement Field(JsonElement a, int n) => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= n ? a[n - 1] : default;
    internal static string? Text(JsonElement a, int n) => Field(a, n) is var v && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static int? Integer(JsonElement a, int n) => Field(a, n) is var v && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number) ? number : null;
    internal static bool Boolean(JsonElement a, int n) => Field(a, n) is var v && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number) && number == 1);
    internal static IEnumerable<JsonElement> Repeated(JsonElement a, int n) => Field(a, n) is var v && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.Clone()).ToArray() : [];
    internal static DateTimeOffset? Timestamp(JsonElement a)
    {
        var seconds = Field(a, 1); var nanos = Integer(a, 2) ?? 0;
        var text = seconds.ValueKind == JsonValueKind.Number ? seconds.GetRawText() : seconds.ValueKind == JsonValueKind.String ? seconds.GetString() : null;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is >= -62135596800 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(number).AddTicks(nanos / 100) : null;
    }
}
