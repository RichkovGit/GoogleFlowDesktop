using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlowStudio.Services;

public sealed record FlowGenerationRequest(
    string ProjectId,
    string ModelKey,
    string Prompt,
    string Kind = "image",
    string AspectRatio = "16:9",
    int Count = 1,
    string? RecaptchaToken = null,
    int? Seed = null,
    IReadOnlyList<FlowImageReference>? Images = null,
    FlowImageReference? FirstFrame = null,
    FlowImageReference? LastFrame = null,
    int? OutputResolution = null,
    FlowImageReference? BaseImage = null);

public sealed record FlowGeneratedMedia(string MediaName, string LocalPath, string Kind, string? WorkflowId);
public sealed record FlowCloudProject(string Id, string Name);

public sealed class FlowGenerationException(string message, int? httpStatusCode = null, int? canonicalCode = null) : Exception(message)
{
    public int? HttpStatusCode { get; } = httpStatusCode;
    public int? CanonicalCode { get; } = canonicalCode;
}

/// <summary>
/// Native HTTP implementation of request builders recovered from Google's public
/// Flow frontend. Uses existing Flow account credentials; no Gemini API billing.
/// Wire evidence and remaining authentication limitations: research/flow_wire_contract.md.
/// No automatic retry of a generation request: retrying could spend credits twice.
/// </summary>
public sealed partial class FlowGenerationService
{
    const string FlowService = "google.internal.labs.aisandbox.proto.flow.v1.FlowService";
    const string VideoService = "google.internal.labs.aisandbox.proto.videofx.v1.VideoFxService";
    const string SandboxService = "google.internal.labs.aisandbox.v1.AiSandbox";
    const int MaxJsonBytes = 16 * 1024 * 1024;
    const long MaxMediaBytes = 1024L * 1024 * 1024;
    readonly HttpClient apiClient;
    readonly HttpClient downloadClient;

    // downloadClient must be separate, with AllowAutoRedirect=false and no
    // credentials/default Authorization. Redirects are validated individually.
    public FlowGenerationService(HttpClient apiClient, HttpClient downloadClient)
    {
        if (ReferenceEquals(apiClient, downloadClient))
            throw new ArgumentException("Для скачивания нужен отдельный HTTP-клиент без авторизации.");
        this.apiClient = apiClient;
        this.downloadClient = downloadClient;
    }

    public async Task<FlowCloudProject> CreateProjectAsync(string name, string bearerToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        name = name.Trim();
        if (name.Length is < 1 or > 200)
            throw new ArgumentException("Название облачного проекта должно содержать от 1 до 200 символов.");
        var token = NormalizeToken(bearerToken);
        // uXa -> BM(parent=projects/*, Project(metadata(display_name)), ClientContext(tool=22)).
        var body = Message((1, "projects/*"), (2, Message((2, Message((1, name))))), (3, Context(null, null)));
        var response = await SendRpcAsync(SandboxService, "CreateProject", body, token, null, cancellationToken);
        var id = StringField(response, 1);
        if (string.IsNullOrWhiteSpace(id))
            throw new FlowGenerationException("Flow не вернул идентификатор созданного проекта.");
        return new(id, StringField(Field(response, 2), 1) ?? name);
    }

    public async Task<IReadOnlyList<FlowGeneratedMedia>> GenerateAsync(
        FlowGenerationRequest request,
        string bearerToken,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        var token = NormalizeToken(bearerToken);
        var video = request.Kind == "video";
        var service = video ? VideoService : FlowService;
        var method = video ? VideoMethod(request) : "BatchGenerateImages";
        progress?.Report("Отправка задания в Google Flow…");
        var response = await SendRpcAsync(service, method,
            video ? BuildVideoRequest(request) : BuildImageRequest(request),
            token, request.RecaptchaToken, cancellationToken);
        var media = RepeatedField(response, video ? 4 : 1).ToArray();
        if (media.Length == 0)
            throw new FlowGenerationException("Flow не вернул медиа для задания. Повторная отправка может дополнительно потратить кредиты.");

        var results = new List<FlowGeneratedMedia>();
        foreach (var submittedMedia in media)
        {
            var current = submittedMedia;
            var name = StringField(current, 1);
            if (string.IsNullOrWhiteSpace(name))
                throw new FlowGenerationException("В ответе Flow отсутствует идентификатор медиа.");
            var deadline = DateTimeOffset.UtcNow.AddMinutes(20);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfMediaFailed(current, token, request.RecaptchaToken);
                var url = MediaUrl(current);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    progress?.Report("Скачивание результата Flow…");
                    var path = await DownloadAsync(url, video ? "video" : "image", outputDirectory, cancellationToken);
                    results.Add(new(name, path, video ? "video" : "image", StringField(current, 3)));
                    break;
                }
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new FlowGenerationException($"Flow ещё обрабатывает медиа {name}. Локальное ожидание завершено; задание в облаке может продолжаться.");
                progress?.Report(video ? "Flow создаёт видео…" : "Flow создаёт изображение…");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (video)
                {
                    // icb -> e9a field 3, repeated Media containing only field 1 name.
                    response = await SendRpcAsync(VideoService, "BatchCheckAsyncVideoGenerationStatus",
                        Message((3, new object?[] { Message((1, name)) })), token, null, cancellationToken);
                    var updated = RepeatedField(response, 3).FirstOrDefault(m => StringField(m, 1) == name);
                    if (updated.ValueKind == JsonValueKind.Array)
                        current = updated;
                }
                else
                {
                    // bVa / cVa: GetMediaRequest.name is field 1; response is Media itself.
                    current = await SendRpcAsync(FlowService, "GetMedia", Message((1, name)), token, null, cancellationToken);
                    if (StringField(current, 1) != name)
                        throw new FlowGenerationException("Flow вернул неожиданное медиа при проверке задания.");
                }
            }
        }
        return results;
    }

    public static object?[] BuildImageRequest(FlowGenerationRequest request)
    {
        Validate(request);
        var context = Context(NormalizeProjectId(request.ProjectId), request.RecaptchaToken);
        var prompt = StructuredPrompt(request.Prompt);
        var ratio = request.AspectRatio switch { "1:1" => 1, "9:16" => 2, "16:9" => 3, "3:4" => 4, "4:3" => 5, _ => throw new ArgumentException("Неподдерживаемые пропорции.") };
        var requests = Enumerable.Range(0, request.Count).Select(index => (object?)Message(
            (3, BuildImageReferences(request)),
            (4, request.Seed ?? RandomNumberGenerator.GetInt32(int.MaxValue)),
            (5, ratio),
            (6, request.ModelKey.Trim()),
            (8, context),
            (9, prompt),
            (11, Array.Empty<object>()),
            (12, Array.Empty<object>()),
            (13, Guid.NewGuid().ToString()),
            (14, Guid.NewGuid().ToString()))).ToArray();
        // Dab -> E5a fields 2=requests, 3=true, 4=context, 5=ES(batch id).
        return Message((2, requests), (3, true), (4, context), (5, Message((1, Guid.NewGuid().ToString()))));
    }

    public static object?[] BuildVideoRequest(FlowGenerationRequest request)
    {
        Validate(request);
        if(request.FirstFrame is not null || request.Images is {Count:>0}) return BuildVideoReferenceRequest(request);
        if (request.AspectRatio is not ("16:9" or "9:16"))
            throw new ArgumentException("Видео Flow поддерживает пропорции 16:9 и 9:16.");
        var context = Context(NormalizeProjectId(request.ProjectId), request.RecaptchaToken);
        var input = Message((3, StructuredPrompt(request.Prompt)));
        var requests = Enumerable.Range(0, request.Count).Select(index => (object?)Message(
            (1, input),
            (2, request.ModelKey.Trim()),
            (3, request.AspectRatio == "9:16" ? 1 : 2),
            (8, ResolutionMessage(request.OutputResolution)),
            (5, Message((5, Guid.NewGuid().ToString()), (6, Guid.NewGuid().ToString()))))).ToArray();
        // NT.Go TEXT -> U8a fields 1=requests, 2=context, 3=ES(batch id).
        return Message((1, requests), (2, context), (3, Message((1, Guid.NewGuid().ToString()))));
    }

    static object?[] Context(string? projectId, string? recaptchaToken) => Message(
        (2, 22),
        (6, projectId),
        (11, string.IsNullOrWhiteSpace(recaptchaToken) ? null : Message((1, recaptchaToken.Trim()), (2, 1))));

    static object?[] StructuredPrompt(string text) => Message((1, new object?[] { Message((1, text.Trim())) }));

    static object?[] Message(params (int Field, object? Value)[] fields)
    {
        var result = new object?[fields.Max(f => f.Field)];
        foreach (var (field, value) in fields)
            result[field - 1] = value;
        // Json+protobuf permits trailing nulls, but omitting them matches jspb.
        var length = result.Length;
        while (length > 0 && result[length - 1] is null)
            length--;
        return result[..length];
    }

    async Task<JsonElement> SendRpcAsync(string service, string method, object?[] body,
        string token, string? recaptchaToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://aisandbox-pa.googleapis.com/$rpc/{service}/{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json+protobuf"));
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json+protobuf");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var bytes = await ReadBoundedAsync(response.Content, MaxJsonBytes, timeout.Token);
        JsonElement json;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            json = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new FlowGenerationException($"Flow вернул нечитаемый ответ (HTTP {(int)response.StatusCode}).", (int)response.StatusCode);
        }
        if (!response.IsSuccessStatusCode)
        {
            var detail = ErrorMessage(json, token, recaptchaToken);
            throw new FlowGenerationException($"Flow: HTTP {(int)response.StatusCode}. {detail}", (int)response.StatusCode, ErrorCode(json));
        }
        if (json.ValueKind != JsonValueKind.Array)
            throw new FlowGenerationException("Flow изменил формат API: ожидался protobuf JSON array.");
        if (Field(json, 1).ValueKind == JsonValueKind.Number && Field(json, 2).ValueKind == JsonValueKind.String)
            throw new FlowGenerationException(ErrorMessage(json, token, recaptchaToken), canonicalCode: ErrorCode(json));
        return json;
    }

    static int? ErrorCode(JsonElement json)
    {
        var code = Field(json, 1);
        if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value is >= 0 and <= 16)
            return value;
        return null;
    }

    static void ThrowIfMediaFailed(JsonElement media, string token, string? recaptchaToken)
    {
        var status = Field(Field(media, 6), 9);
        var code = Field(status, 1);
        if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value is 4 or 5 or 7)
        {
            var detail = ErrorMessage(Field(status, 2), token, recaptchaToken);
            throw new FlowGenerationException(value == 5 ? "Создание медиа отменено в Google Flow." : $"Flow не создал медиа. {detail}");
        }
    }

    public static string? MediaUrl(JsonElement media)
    {
        // OK() in the public frontend: generated and uploaded image/video paths.
        var image = Field(media, 7);
        var video = Field(media, 8);
        return StringField(Field(image, 1), 14) ?? StringField(Field(image, 2), 4)
            ?? StringField(Field(video, 1), 9) ?? StringField(Field(video, 5), 3);
    }

    static JsonElement Field(JsonElement array, int field) =>
        array.ValueKind == JsonValueKind.Array && array.GetArrayLength() >= field ? array[field - 1] : default;

    static string? StringField(JsonElement array, int field)
    {
        var value = Field(array, field);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    }

    static IEnumerable<JsonElement> RepeatedField(JsonElement array, int field)
    {
        var value = Field(array, field);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.Clone()).ToArray() : [];
    }

    static string ErrorMessage(JsonElement json, string token, string? recaptchaToken)
    {
        var message = StringField(json, 2);
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String)
            message = value.GetString();
        message ??= "Google отклонил запрос. Проверьте доступ аккаунта, модель, кредиты и reCAPTCHA.";
        message = message.Replace(token, "[скрыто]", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(recaptchaToken))
            message = message.Replace(recaptchaToken.Trim(), "[скрыто]", StringComparison.Ordinal);
        return new string(message.Where(c => !char.IsControl(c)).Take(500).ToArray());
    }

    public static bool IsAllowedMediaUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host.Equals("storage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("flow-content.google", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    async Task<string> DownloadAsync(string url, string kind, string outputDirectory, CancellationToken cancellationToken)
    {
        if (downloadClient.DefaultRequestHeaders.Authorization is not null ||
            downloadClient.DefaultRequestHeaders.ProxyAuthorization is not null ||
            downloadClient.DefaultRequestHeaders.Contains("Cookie") ||
            downloadClient.DefaultRequestHeaders.Contains("x-goog-api-key"))
            throw new FlowGenerationException("Клиент загрузки содержит авторизацию; скачивание остановлено для защиты токена.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedMediaUri(uri))
            throw new FlowGenerationException("Flow вернул медиа с неподдерживаемого адреса. Загрузка остановлена.");
        for (var redirect = 0; redirect < 6; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.RequestMessage?.RequestUri is { } actual && !IsAllowedMediaUri(actual))
                throw new FlowGenerationException("Ссылка на медиа перенаправила загрузку за пределы разрешённых серверов.");
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null || !Uri.TryCreate(uri, location, out var next) || !IsAllowedMediaUri(next))
                    throw new FlowGenerationException("Сервер медиа вернул неподдерживаемое перенаправление.");
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new FlowGenerationException($"Не удалось скачать медиа Flow: HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaxMediaBytes)
                throw new FlowGenerationException("Медиа Flow превышает предел загрузки 1 ГБ.");
            var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            string? extension = mime switch
            {
                "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp",
                "video/mp4" => ".mp4", "video/webm" => ".webm",
                "application/octet-stream" => null,
                _ => throw new FlowGenerationException("Сервер Flow вернул неподдерживаемый тип медиа.")
            };
            if (extension is not null && (kind == "video" ? !mime!.StartsWith("video/", StringComparison.Ordinal) : !mime!.StartsWith("image/", StringComparison.Ordinal)))
                throw new FlowGenerationException("Тип загруженного медиа не соответствует заданию Flow.");
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(Path.GetFullPath(outputDirectory), $"flow-{Guid.NewGuid():N}{extension ?? ".bin"}");
            var temporary = path + ".part";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        total += read;
                        if (total > MaxMediaBytes)
                            throw new FlowGenerationException("Медиа Flow превышает предел загрузки 1 ГБ.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    if (total == 0)
                        throw new FlowGenerationException("Flow вернул пустой файл медиа.");
                }
                if (extension is null)
                {
                    // Signed GCS objects can use application/octet-stream. Accept
                    // those only when their actual header identifies supported media.
                    var header = new byte[16];
                    int length;
                    await using (var input = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 16, true))
                        length = await input.ReadAsync(header, cancellationToken);
                    extension = DetectMediaExtension(header, length, kind);
                    if (extension is null)
                        throw new FlowGenerationException("Файл Flow не распознан как поддерживаемое медиа.");
                    path = Path.ChangeExtension(path, extension);
                }
                File.Move(temporary, path);
                return path;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        throw new FlowGenerationException("Слишком много перенаправлений при скачивании медиа Flow.");
    }

    static string? DetectMediaExtension(byte[] header, int length, string kind)
    {
        if (kind == "video")
        {
            if (length >= 12 && Encoding.ASCII.GetString(header, 4, 4) == "ftyp") return ".mp4";
            if (length >= 4 && header[0] == 0x1a && header[1] == 0x45 && header[2] == 0xdf && header[3] == 0xa3) return ".webm";
            return null;
        }
        if (length >= 8 && header.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (length >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255) return ".jpg";
        if (length >= 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP") return ".webp";
        return null;
    }

    static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximum)
                throw new FlowGenerationException("Ответ Flow превышает допустимый размер.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    static string NormalizeToken(string token)
    {
        token = token.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16384 ||
            token.Any(c => !char.IsAsciiLetterOrDigit(c) && "-._~+/=".IndexOf(c) < 0))
            throw new ArgumentException("Укажите действующий OAuth access token Google Flow.");
        return token;
    }

    static string NormalizeProjectId(string projectId)
    {
        projectId = projectId.Trim();
        if (projectId.StartsWith("projects/", StringComparison.Ordinal))
            projectId = projectId[9..];
        return Guid.TryParse(projectId, out var id) ? id.ToString() : throw new ArgumentException("Нужен UUID облачного проекта Google Flow.");
    }

    static void Validate(FlowGenerationRequest request)
    {
        _ = NormalizeProjectId(request.ProjectId);
        if (request.Kind is not ("image" or "video"))
            throw new ArgumentException("Неподдерживаемый вид генерации Flow.");
        if (string.IsNullOrWhiteSpace(request.ModelKey) || request.ModelKey.Length > 250 || request.ModelKey.Any(char.IsWhiteSpace))
            throw new ArgumentException("Выберите реальный ключ модели из каталога Flow.");
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 30000)
            throw new ArgumentException("Добавьте описание длиной до 30 000 символов.");
        if (request.Count is < 1 or > 4)
            throw new ArgumentException("Можно отправить от 1 до 4 вариантов за один запрос.");
        if (request.RecaptchaToken?.Length > 32768 || request.RecaptchaToken?.Any(char.IsControl) == true)
            throw new ArgumentException("Неверный формат токена reCAPTCHA.");
        if (request.Seed is < 0)
            throw new ArgumentException("Seed должен быть неотрицательным.");
        if (request.Kind == "video" && request.Seed is not null)
            throw new ArgumentException("У подтверждённого видеозапроса Flow нет поля seed.");
        ValidateReferenceInputs(request);
    }
}
