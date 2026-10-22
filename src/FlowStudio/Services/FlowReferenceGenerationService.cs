using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlowStudio.Services;

public sealed record FlowImageCrop(double Top, double Left, double Bottom, double Right);
public sealed record FlowImageReference(string MediaName, FlowImageCrop? Crop = null);
public sealed record FlowUploadedImage(string MediaName, string? WorkflowId, string MimeType)
{
    public FlowImageReference AsReference() => new(MediaName);
}

/// <summary>Source-confirmed native image upload, image references, first/last frame and ingredients request builders.</summary>
public sealed partial class FlowGenerationService
{
    const int MaxUploadImageBytes=8*1024*1024;

    /// <summary>Uploads the user's explicitly selected image once. Upload and generation need separate action-scoped CAPTCHA tokens if Google requires them.</summary>
    public async Task<FlowUploadedImage> UploadImageAsync(string projectId,string filePath,string credential,
        string? recaptchaToken=null,CancellationToken cancellationToken=default)
    {
        var project=NormalizeProjectId(projectId);
        var token=NormalizeToken(credential);
        if(recaptchaToken?.Length>32768 || recaptchaToken?.Any(char.IsControl)==true) throw new ArgumentException("Неверный токен проверки загрузки.");
        var path=Path.GetFullPath(filePath);
        var file=new FileInfo(path);
        if(!file.Exists) throw new FileNotFoundException("Изображение для загрузки не найдено.");
        if(file.Length is <=0 or >MaxUploadImageBytes) throw new ArgumentException("Загрузите изображение размером до 8 МБ.");
        var bytes=await File.ReadAllBytesAsync(path,cancellationToken).ConfigureAwait(false);
        if(bytes.Length is <=0 or >MaxUploadImageBytes) throw new ArgumentException("Размер изображения изменился во время чтения.");
        var mime=ImageMime(bytes)??throw new ArgumentException("Поддерживаются настоящие PNG, JPEG и WebP.");
        // zab -> UploadImage O7a: ClientContext1, bytes2, mime3, user-upload4=true,
        // hidden8=false, filename9. No browser or signed upload URL is involved.
        var body=Message((1,Context(project,recaptchaToken)),(2,Convert.ToBase64String(bytes)),(3,mime),(4,true),(8,false),(9,Path.GetFileName(path)));
        var response=await SendRpcAsync(FlowService,"UploadImage",body,token,recaptchaToken,cancellationToken).ConfigureAwait(false);
        var media=Field(response,1);
        var name=StringField(media,1);
        if(string.IsNullOrWhiteSpace(name)) throw new FlowGenerationException("Google не вернул загруженное изображение.");
        _=ReferenceId(name);
        return new(name,StringField(media,3),mime);
    }

    static string? ImageMime(byte[] bytes)
    {
        if(bytes.Length>=8 && bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})) return "image/png";
        if(bytes.Length>=3 && bytes[0]==255 && bytes[1]==216 && bytes[2]==255) return "image/jpeg";
        if(bytes.Length>=12 && bytes.AsSpan(0,4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8,4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    static string VideoMethod(FlowGenerationRequest request) => request.FirstFrame is not null
        ?request.LastFrame is not null?"BatchAsyncGenerateVideoStartAndEndImage":"BatchAsyncGenerateVideoStartImage"
        :request.Images is {Count:>0}?"BatchAsyncGenerateVideoReferenceImages":"BatchAsyncGenerateVideoText";

    public static object?[] BuildVideoReferenceRequest(FlowGenerationRequest request)
    {
        Validate(request);
        if(request.Kind!="video") throw new ArgumentException("Кадры и ингредиенты этого запроса требуют режима видео.");
        if(request.FirstFrame is null && request.Images is not {Count:>0}) throw new ArgumentException("Добавьте первый кадр или изображения-ингредиенты.");
        if(request.AspectRatio is not("16:9" or "9:16")) throw new ArgumentException("Видео Flow поддерживает 16:9 и 9:16.");
        var aspect=request.AspectRatio=="9:16"?1:2;
        var input=Message((3,StructuredPrompt(request.Prompt)));
        var context=Context(NormalizeProjectId(request.ProjectId),request.RecaptchaToken);
        var requests=Enumerable.Range(0,request.Count).Select(_=>
        {
            var metadata=Message((5,Guid.NewGuid().ToString()),(6,Guid.NewGuid().ToString()));
            if(request.FirstFrame is {} first)
            {
                // NT.Go START_FRAME -> J8a: input1/model2/aspect3/start5/metadata6/resolution10.
                if(request.LastFrame is null) return (object?)Message((1,input),(2,request.ModelKey.Trim()),(3,aspect),
                    (5,VideoImageReference(first)),(6,metadata),(10,ResolutionMessage(request.OutputResolution)));
                // START_END_FRAMES -> C8a: input1/model2/aspect3/start5/end6/metadata7.
                return Message((1,input),(2,request.ModelKey.Trim()),(3,aspect),(5,VideoImageReference(first)),
                    (6,VideoImageReference(request.LastFrame)),(7,metadata));
            }
            // REFERENCES -> v8a: input1/references2/model3/aspect4/metadata6/resolution12.
            return Message((1,input),(2,request.Images!.Select(image=>(object?)VideoImageReference(image)).ToArray()),
                (3,request.ModelKey.Trim()),(4,aspect),(6,metadata),(12,ResolutionMessage(request.OutputResolution)));
        }).ToArray();
        // N8a/G8a/z8a: repeated requests1, ClientContext2, ES batch metadata3.
        return Message((1,requests),(2,context),(3,Message((1,Guid.NewGuid().ToString()))));
    }

    static object?[] BuildImageReferences(FlowGenerationRequest request)
    {
        var result=new List<object?>();
        // Dab -> w4a: mediaId1, reference role5=1; base image role5=2.
        if(request.BaseImage is {} original) result.Add(Message((1,ReferenceId(original.MediaName)),(5,2)));
        foreach(var image in request.Images??Array.Empty<FlowImageReference>()) result.Add(Message((1,ReferenceId(image.MediaName)),(5,1)));
        return result.ToArray();
    }

    static object?[] VideoImageReference(FlowImageReference image) => Message(
        (2,ReferenceId(image.MediaName)), // WQ.oneof field2=existing mediaId (field1 is raw bytes).
        (6,image.Crop is {} crop?Message((1,crop.Top),(2,crop.Left),(3,crop.Bottom),(4,crop.Right)):null));
    static object?[]? ResolutionMessage(int? resolution) => resolution is null or 1?null:Message((1,resolution));

    static string ReferenceId(string name)
    {
        if(string.IsNullOrWhiteSpace(name) || name.Length>2048 || name.Any(char.IsControl) || name.Contains("://",StringComparison.Ordinal))
            throw new ArgumentException("Неверный идентификатор изображения Flow.");
        // Client media identifiers are reduced with _.pu() (last resource-name component).
        var id=name.Trim().Split('/')[^1];
        if(id.Length is <1 or >512 || id is "." or ".." || id.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('-' or '_' or '.')))
            throw new ArgumentException("Неверный идентификатор изображения Flow.");
        return id;
    }

    static void ValidateReferenceInputs(FlowGenerationRequest request)
    {
        var images=request.Images??Array.Empty<FlowImageReference>();
        if(images.Count>14) throw new ArgumentException("Этот клиент поддерживает до 14 изображений в запросе; модель может устанавливать меньший предел.");
        if(request.OutputResolution is not(null or 1 or 2 or 3 or 4)) throw new ArgumentException("Неподдерживаемое разрешение Flow.");
        if(request.Kind=="image" && (request.FirstFrame is not null || request.LastFrame is not null || request.OutputResolution is not null))
            throw new ArgumentException("Первый/последний кадр и разрешение видео применимы только к видео.");
        if(request.Kind=="video" && request.BaseImage is not null) throw new ArgumentException("Базовое изображение предназначено для редактирования изображения.");
        if(request.LastFrame is not null && request.FirstFrame is null) throw new ArgumentException("Для последнего кадра сначала добавьте первый.");
        if(request.FirstFrame is not null && images.Count>0) throw new ArgumentException("Выберите либо кадры, либо изображения-ингредиенты.");
        foreach(var image in images.Concat(new[]{request.FirstFrame,request.LastFrame,request.BaseImage}.Where(image=>image is not null).Select(image=>image!)))
        {
            if(image is null) throw new ArgumentException("Пустая ссылка на изображение.");
            _=ReferenceId(image.MediaName);
            if(image.Crop is {} crop)
            {
                if(request.Kind=="image") throw new ArgumentException("Обрезка референсов поддерживается в видеозапросе.");
                if(!double.IsFinite(crop.Top)||!double.IsFinite(crop.Left)||!double.IsFinite(crop.Bottom)||!double.IsFinite(crop.Right)||
                    crop.Top<0||crop.Left<0||crop.Bottom>1||crop.Right>1||crop.Bottom<=crop.Top||crop.Right<=crop.Left)
                    throw new ArgumentException("Область кадрирования должна находиться внутри изображения (0–1).");
            }
        }
    }
}
