using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

public sealed record FlowAgentSessionSummary(string Id, string Title, DateTimeOffset? UpdatedAt);
public sealed record FlowAgentTurn(string UserText, string AssistantText, bool RequiresApproval = false, int? CreditCost = null, bool CanApprove = true);
public sealed record FlowAgentConversation(string Id, string Title, IReadOnlyList<FlowAgentTurn> Turns);
public sealed record FlowAgentInstruction(string Id,string Title,string Description,bool Enabled,IReadOnlyList<string>? ImageMediaIds=null,IReadOnlyList<string>? EntityIds=null,IReadOnlyList<string>? LikenessIds=null);
public sealed record FlowAgentDefaults(string ModelKey,int AspectRatio,int Count);
public sealed record FlowAgentSettings(int ApprovalPolicy,IReadOnlyList<FlowAgentInstruction> Instructions,FlowAgentDefaults? ImageDefaults,FlowAgentDefaults? VideoDefaults);

/// <summary>First-party Flow Creation Agent, through its native same-origin RPC transport. No Gemini key or browser execution.</summary>
public sealed class FlowAgentService : IDisposable
{
    const string Service = "google.internal.labs.aisandbox.proto.flow.agent.v1.FlowCreationAgentService";
    readonly HttpClient api;
    readonly FlowCookieAuthHandler auth;
    readonly HttpClient streaming;
    readonly bool ownsStreaming;
    public FlowAgentService(HttpClient apiClient, FlowCookieAuthHandler sessionHandler,HttpClient? streamClient=null)
    {
        api=apiClient;auth=sessionHandler;ownsStreaming=streamClient is null;
        streaming=streamClient??new HttpClient(new SocketsHttpHandler {AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(20)}){Timeout=TimeSpan.FromMinutes(11)};
    }

    public async Task<FlowAgentSettings> GetSettingsAsync(string projectId,string credential,CancellationToken ct=default)
    {
        projectId=FlowCloudLibraryService.NormalizeProjectId(projectId);
        // gU.Wb -> AiSandbox.GetProject tools/PINHOLE/projects/id, AM.agentInfo6.
        var project=await FlowNativeRpc.SendAsync(api,"google.internal.labs.aisandbox.v1.AiSandbox","GetProject",new object?[]{"tools/PINHOLE/projects/"+projectId},credential,ct);
        return ParseSettings(FlowNativeRpc.Field(project,6));
    }
    internal static FlowAgentSettings ParseSettings(JsonElement settings)
    {
        var instructions=FlowNativeRpc.Repeated(FlowNativeRpc.Field(settings,2),2).Select(card=>new FlowAgentInstruction(
            FlowNativeRpc.Text(card,1)??Guid.NewGuid().ToString(),FlowNativeRpc.Text(card,2)??"",FlowNativeRpc.Text(card,3)??"",FlowNativeRpc.Boolean(card,4),
            Strings(card,5),Strings(card,6),Strings(card,7))).ToArray();
        var defaults=FlowNativeRpc.Field(settings,3);
        var image=FlowNativeRpc.Field(defaults,1);var video=FlowNativeRpc.Field(defaults,2);
        return new(FlowNativeRpc.Integer(settings,4)==2?2:1,instructions,
            image.ValueKind==JsonValueKind.Array?new(FlowNativeRpc.Text(image,4)??"",FlowNativeRpc.Integer(image,2)??0,FlowNativeRpc.Integer(image,3)??1):null,
            video.ValueKind==JsonValueKind.Array?new(FlowNativeRpc.Text(video,1)??"",FlowNativeRpc.Integer(video,2)??0,FlowNativeRpc.Integer(video,3)??1):null);
    }
    public Task UpdateApprovalPolicyAsync(string projectId,int policy,string credential,CancellationToken ct=default)
    {
        if(policy is not(1 or 2))throw new ArgumentException("Выберите подтверждение перед генерацией: всегда или никогда.");
        return UpdateSettingsAsync(projectId,new object?[]{null,null,null,policy},["credit_spend_approval_policy"],credential,ct);
    }
    public Task UpdateInstructionsAsync(string projectId,IReadOnlyList<FlowAgentInstruction> instructions,string credential,CancellationToken ct=default)
    {
        if(instructions.Count>100||instructions.Count(i=>i.Enabled)>3||instructions.Any(i=>string.IsNullOrWhiteSpace(i.Id)||i.Title.Length>1000||i.Description.Length>30000))
            throw new ArgumentException("Можно включить до трёх инструкций агента; проверьте их название и длину.");
        var cards=instructions.Select(i=>(object?)new object?[]{i.Id,i.Title,i.Description,i.Enabled,i.ImageMediaIds??[],i.EntityIds??[],i.LikenessIds??[]}).ToArray();
        // Obc: SR.project_brief2 -> RR{enabled1,cards2}, masked update preserves all other settings.
        return UpdateSettingsAsync(projectId,new object?[]{null,new object?[]{instructions.Any(i=>i.Enabled),cards}},["project_brief"],credential,ct);
    }
    public Task UpdateDefaultsAsync(string projectId,string kind,FlowAgentDefaults defaults,string credential,CancellationToken ct=default)
    {
        if(kind is not("image" or "video")||string.IsNullOrWhiteSpace(defaults.ModelKey)||defaults.Count is <1 or >4||
            (kind=="video"?defaults.AspectRatio is not(1 or 2):defaults.AspectRatio is <1 or >5))
            throw new ArgumentException("Некорректные параметры агента.");
        object?[] value=kind=="image"?[null,defaults.AspectRatio,defaults.Count,defaults.ModelKey]:[defaults.ModelKey,defaults.AspectRatio,defaults.Count];
        object?[] pair=kind=="image"?[value]:[null,value];
        return UpdateSettingsAsync(projectId,new object?[]{null,null,pair},["default_generation_settings."+(kind=="image"?"image_defaults":"video_defaults")],credential,ct);
    }
    async Task UpdateSettingsAsync(string projectId,object?[] settings,string[] mask,string credential,CancellationToken ct)
    {
        projectId=FlowCloudLibraryService.NormalizeProjectId(projectId);
        await FlowNativeRpc.SendAsync(api,"google.internal.labs.aisandbox.v1.AiSandbox","UpdateAgentInfo",new object?[]{"projects/"+projectId,settings,new object?[]{mask}},credential,ct);
    }
    static string[] Strings(JsonElement value,int field)=>FlowNativeRpc.Repeated(value,field).Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()!).ToArray();

    public async Task<IReadOnlyList<FlowAgentSessionSummary>> ListSessionsAsync(string projectId, string credential, CancellationToken ct = default)
    {
        projectId=FlowCloudLibraryService.NormalizeProjectId(projectId);
        var response=await FlowNativeRpc.SendAsync(api,Service,"ListSessions",new object?[]{projectId},credential,ct);
        return FlowNativeRpc.Repeated(response,1).Select(ParseSummary).Where(s=>s.Id.Length>0).ToArray();
    }
    public async Task<FlowAgentConversation> GetSessionAsync(string sessionId,string credential,CancellationToken ct=default)
    {
        ValidateSessionId(sessionId);
        var response=await FlowNativeRpc.SendAsync(api,Service,"GetSession",new object?[]{sessionId},credential,ct);
        return ParseConversation(response);
    }
    public async Task<FlowAgentConversation> CreateSessionAsync(string projectId,string credential,CancellationToken ct=default)
    {
        projectId=FlowCloudLibraryService.NormalizeProjectId(projectId);
        // XZb project1 + seed3. Source rR/NWa derives the server session UUID from this seed.
        var response=await FlowNativeRpc.SendAsync(api,Service,"CreateSession",new object?[]{projectId,null,Guid.NewGuid().ToString()},credential,ct);
        return ParseConversation(response);
    }

    public async Task<FlowAgentConversation> SendAsync(string projectId,string sessionId,string prompt,int turnNumber,string credential,
        IProgress<string>? progress=null,CancellationToken ct=default,int permissionAction=0,string? recaptchaToken=null)
    {
        projectId=FlowCloudLibraryService.NormalizeProjectId(projectId);ValidateSessionId(sessionId);
        if(string.IsNullOrWhiteSpace(prompt)||prompt.Length>30000||turnNumber<1||permissionAction is <0 or >3)throw new ArgumentException("Некорректное сообщение агенту Flow.");
        if(credential!=FlowCookieAuthHandler.SessionCredentialMarker)throw new InvalidOperationException("Агент Flow использует сохранённый вход Google.");
        var captured=auth.Session??throw new InvalidOperationException("Войдите в Google перед запуском агента.");
        // I0b: StreamChat session1, S0b content2, context4, optional approval action5.
        // UK.parts1 -> ZK.text1. CHAT_GENERATION token belongs to this one action only.
        object?[] promptContent=[new object?[]{new object?[]{new object?[]{prompt.Trim()}}}];
        object?[] context=["projects/"+projectId,null,string.IsNullOrWhiteSpace(recaptchaToken)?null:new object?[]{recaptchaToken,1},null,"",turnNumber];
        object?[] payload=[sessionId,promptContent,null,context,permissionAction==0?null:permissionAction];
        var uri=StreamEndpoint(captured);
        using var request=new HttpRequestMessage(HttpMethod.Post,uri);
        var cookie=FlowCookieAuthHandler.BuildCookieHeader(captured.Cookies,uri,DateTimeOffset.UtcNow);
        if(cookie.Length==0)throw new FlowCloudLibraryException("Сессия Google истекла. Подключите аккаунт заново.");
        request.Headers.TryAddWithoutValidation("Cookie",cookie);
        request.Headers.TryAddWithoutValidation("Origin","https://flow.google.com");
        request.Headers.Referrer=new Uri("https://flow.google.com/");
        request.Headers.TryAddWithoutValidation("X-Same-Domain","1");
        request.Headers.TryAddWithoutValidation("X-Client-Deadline-Ms","600000");
        if(captured.UserAgent.Length is >0 and <1024 && !captured.UserAgent.Any(char.IsControl))request.Headers.TryAddWithoutValidation("User-Agent",captured.UserAgent);
        var xsrf=captured.Wiz.TryGetValue("SNlM0e",out var token)?token:null;
        if(string.IsNullOrWhiteSpace(xsrf))throw new FlowCloudLibraryException("Сессия Flow требует обновления входа.");
        // Public epa()/cpa(): generic Or field2 contains stringified message.
        request.Content=new FormUrlEncodedContent(new Dictionary<string,string>{["f.req"]=JsonSerializer.Serialize(new object?[]{null,JsonSerializer.Serialize(payload)}),["at"]=xsrf});
        using var response=await streaming.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(response.RequestMessage?.RequestUri is {} actual&&actual!=uri)throw new FlowCloudLibraryException("Поток агента перенаправил запрос. Подключение остановлено.");
        if(response.Headers.TryGetValues("Set-Cookie",out var rotated))auth.MergeResponseCookies(captured,uri,rotated);
        if(!response.IsSuccessStatusCode)throw new FlowCloudLibraryException($"Агент Flow отклонил запрос (HTTP {(int)response.StatusCode}). Запрос автоматически не повторяется.",(int)response.StatusCode);
        await using var input=await response.Content.ReadAsStreamAsync(ct);
        using var reader=new StreamReader(input,new UTF8Encoding(false,true),false,4096,true);
        var pending=new StringBuilder();var readBuffer=new char[4096];var text="";long total=0;
        var started=false;
        while(true)
        {
            var read=await reader.ReadAsync(readBuffer.AsMemory(),ct);
            if(read==0)break;
            total+=read;if(total>32*1024*1024)throw new FlowCloudLibraryException("Ответ агента Flow превышает допустимый размер.");
            pending.Append(readBuffer,0,read);
            foreach(var frame in TakeFrames(pending,ref started))
            {
                using var json=JsonDocument.Parse(frame,new JsonDocumentOptions {MaxDepth=96});
                foreach(var row in FrameRows(json.RootElement))
                {
                    if(FlowNativeRpc.Text(row,1)=="er")
                    {
                        var errorCode=FlowNativeRpc.Integer(row,10)??13;
                        throw StreamError(errorCode,FlowCookieAuthHandler.ClassifyFailure(row));
                    }
                    if(FlowNativeRpc.Text(row,1)!="wrb.fr")continue;
                    if(FlowNativeRpc.Field(row,6).ValueKind==JsonValueKind.Array && FlowNativeRpc.Integer(FlowNativeRpc.Field(row,6),1) is { } code && code!=0)
                    {
                        var reason=FlowCookieAuthHandler.ClassifyFailure(FlowNativeRpc.Field(row,6));
                        throw StreamError(code,reason);
                    }
                    var serialized=FlowNativeRpc.Text(row,3);
                    if(serialized is null)continue;
                    using var chunk=JsonDocument.Parse(serialized,new JsonDocumentOptions {MaxDepth=96});
                    var legacy=FlowNativeRpc.Field(chunk.RootElement,1);
                    var piece=StructText(FlowNativeRpc.Field(legacy,1),"text");
                    text=MergeText(text,piece);
                    if(text.Length>0)progress?.Report(text);
                }
            }
        }
        if(pending.ToString().Trim().Length>0)throw new FlowCloudLibraryException("Поток агента Flow завершился не полностью. Обновите историю сеанса.");
        // Read persisted server history, including generated media/tool outcomes.
        return await GetSessionAsync(sessionId,credential,ct);
    }

    internal static FlowAgentConversation ParseConversation(JsonElement root)
    {
        var session=ParseSummary(FlowNativeRpc.Field(root,1));
        var turns=new List<FlowAgentTurn>();
        foreach(var turn in FlowNativeRpc.Repeated(root,2))
        {
            var user=PromptText(FlowNativeRpc.Field(FlowNativeRpc.Field(turn,1),1));
            var text="";var approval=false;var canApprove=true;int? cost=null;
            foreach(var chunk in FlowNativeRpc.Repeated(turn,2))
            {
                text=MergeText(text,StructText(FlowNativeRpc.Field(chunk,1),"text"));
                foreach(var e in FlowNativeRpc.Repeated(chunk,2))
                {
                    var tool=FlowNativeRpc.Field(e,4);
                    if(FlowNativeRpc.Text(tool,2)=="ask_for_permission")
                    {
                        approval=true;
                        cost=StructInteger(FlowNativeRpc.Field(tool,3),"total_cost");
                    }
                }
            }
            // New event history format (U0b/HYb), field10 response text,15 approval.
            foreach(var e in FlowNativeRpc.Repeated(turn,3))
            {
                var content=FlowNativeRpc.Field(e,10);
                if(FlowNativeRpc.Integer(content,3)==1)text=MergeText(text,FlowNativeRpc.Text(content,1)??"");
                var permission=FlowNativeRpc.Field(e,15);
                if(permission.ValueKind==JsonValueKind.Array){approval=true;cost=FlowNativeRpc.Integer(permission,3);canApprove=false;}
                if(FlowNativeRpc.Field(e,21).ValueKind==JsonValueKind.Array)approval=false;
            }
            if(user.Length>0||text.Length>0||approval)turns.Add(new(user,text,approval,cost,canApprove));
        }
        return new(session.Id,session.Title,turns);
    }
    static FlowAgentSessionSummary ParseSummary(JsonElement value)
    {
        var metadata=FlowNativeRpc.Field(value,2);
        return new(FlowNativeRpc.Text(value,1)??"",FlowNativeRpc.Text(metadata,1)??"Сеанс без названия",FlowNativeRpc.Timestamp(FlowNativeRpc.Field(metadata,3)));
    }
    static string PromptText(JsonElement prompt)=>string.Join("",FlowNativeRpc.Repeated(prompt,1).Select(part=>FlowNativeRpc.Text(part,1)).Where(t=>t is not null));
    internal static string MergeText(string previous,string piece)=>piece.Length==0?previous:previous.Length==0||piece.StartsWith(previous,StringComparison.Ordinal)?piece:previous.EndsWith(piece,StringComparison.Ordinal)?previous:previous+piece;
    static string StructText(JsonElement value,string key)
    {
        foreach(var entry in FlowNativeRpc.Repeated(value,1)) if(FlowNativeRpc.Text(entry,1)==key)return FlowNativeRpc.Text(FlowNativeRpc.Field(entry,2),3)??"";
        return "";
    }
    static int? StructInteger(JsonElement value,string key)
    {
        foreach(var entry in FlowNativeRpc.Repeated(value,1)) if(FlowNativeRpc.Text(entry,1)==key)
        {
            var number=FlowNativeRpc.Field(FlowNativeRpc.Field(entry,2),2);
            return number.ValueKind==JsonValueKind.Number&&number.TryGetDouble(out var n)&&n is >=0 and <=int.MaxValue?(int)n:null;
        }
        return null;
    }
    static Uri StreamEndpoint(FlowBrowserSession captured)
    {
        var values=new Dictionary<string,string>{["rt"]="c"};
        foreach(var (key,name) in new[]{("FdrFJe","f.sid"),("cfb2h","bl")})if(captured.Wiz.TryGetValue(key,out var value)&&!string.IsNullOrEmpty(value))values[name]=value;
        return new Uri("https://flow.google.com/_/AiSandboxAngularFrontend/data/"+Service+"/StreamChat?"+string.Join("&",values.Select(v=>Uri.EscapeDataString(v.Key)+"="+Uri.EscapeDataString(v.Value))));
    }
    internal static IReadOnlyList<string> TakeFrames(StringBuilder buffer,ref bool started)
    {
        var frames=new List<string>();
        var text=buffer.ToString();
        if(!started)
        {
            text=text.TrimStart();
            if(text.Length<4)return frames;
            if(text.StartsWith(")]}'",StringComparison.Ordinal))
            {
                var nl=text.IndexOf('\n');if(nl<0)return frames;
                text=text[(nl+1)..];
            }
            started=true;
        }
        while(true)
        {
            text=text.TrimStart();if(text.Length==0)break;
            var newline=text.IndexOf('\n');if(newline<0)break;
            if(!int.TryParse(text.AsSpan(0,newline),NumberStyles.None,CultureInfo.InvariantCulture,out var length)||length<1||length>32*1024*1024)throw new FlowCloudLibraryException("Google изменил формат потока агента.");
            if(text.Length-newline<length)break;
            frames.Add(text.Substring(newline,length));text=text[(newline+length)..];
        }
        buffer.Clear();buffer.Append(text);return frames;
    }
    static IEnumerable<JsonElement> FrameRows(JsonElement root)
    {
        if(root.ValueKind!=JsonValueKind.Array)yield break;
        if(root.GetArrayLength()>0&&root[0].ValueKind==JsonValueKind.String){yield return root;yield break;}
        foreach(var item in root.EnumerateArray())if(item.ValueKind==JsonValueKind.Array)yield return item;
    }
    static void ValidateSessionId(string value){if(!Guid.TryParse(value,out _))throw new ArgumentException("Некорректный сеанс агента Flow.");}
    static FlowCloudLibraryException StreamError(int code,string? reason)=>new(reason switch
    {
        "captcha"=>"Google требует новую reCAPTCHA для агента Flow. Браузер для работы агента не запускается.",
        "country"=>"Google сообщил о региональном ограничении агента Flow.",
        "credits"=>"Google сообщил о недостатке кредитов для действия агента Flow.",
        _=>code switch{3=>"Google отклонил формат запроса агента Flow (код 3).",7=>"Google не разрешил действие агента Flow (код 7). Причина не уточнена.",16=>"Google не принял сессию агента Flow (код 16).",_=>$"Google отклонил действие агента Flow (код {code})."}
    },code switch{3=>400,7=>403,8=>429,16=>401,_=>502},code,reason);
    public void Dispose(){if(ownsStreaming)streaming.Dispose();}
}
