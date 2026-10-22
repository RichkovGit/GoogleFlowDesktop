using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Globalization;
using System.IO;

namespace FlowStudio.Services;

public sealed partial class FlowBrowserRuntimeService
{
    public bool EnableCountryCompatibility { get; set; }
    string? preparedProject;
    bool compatibilityInstalled;
    bool responseInterceptionInstalled;
    readonly HashSet<string> activeAgentRequests=[];
    readonly Queue<JsonElement> regionResponses=[];
    readonly HashSet<string> observedRpcIds=[];
    int regionResponseChanges;
    readonly Dictionary<string,string> actionRequests=[];
    static readonly HashSet<string> GenerationRpcIds=["YhhmEf","eb1hJf","nprQif","MZZa6b","ogiZ0b"];
    int actionAccepted;
    string? actionFailure;
    void ObserveNetworkEvent(string method,JsonElement parameters)
    {
        if(method=="Fetch.requestPaused"){regionResponses.Enqueue(parameters.Clone());return;}
        if(!parameters.TryGetProperty("requestId",out var id))return;
        var key=id.GetString();if(key==null)return;
        if(method=="Network.requestWillBeSent"&&parameters.TryGetProperty("request",out var request)&&request.TryGetProperty("url",out var url)&&Uri.TryCreate(url.GetString(),UriKind.Absolute,out var endpoint)&&endpoint.Host=="flow.google.com")
        {
            if(endpoint.AbsolutePath==AgentPath){activeAgentRequests.Add(key);actionRequests[key]="agent";}
            foreach(var pair in endpoint.Query.TrimStart('?').Split('&'))if(pair.StartsWith("rpcids=",StringComparison.Ordinal))
                foreach(var rpc in Uri.UnescapeDataString(pair[7..]).Split(','))if(rpc.Length is >0 and <40&&rpc.All(char.IsAsciiLetterOrDigit)&&observedRpcIds.Count<50){observedRpcIds.Add(rpc);if(GenerationRpcIds.Contains(rpc))actionRequests[key]=rpc;}
        }
        else if(method=="Network.responseReceived"&&actionRequests.ContainsKey(key)&&parameters.TryGetProperty("response",out var response)&&response.TryGetProperty("status",out var responseStatus)&&responseStatus.GetDouble()>=400)actionFailure="Google отклонил действие: HTTP "+responseStatus.GetDouble().ToString(CultureInfo.InvariantCulture)+". Автоматического повтора не было.";
        else if(method is "Network.loadingFinished" or "Network.loadingFailed")activeAgentRequests.Remove(key);
    }
    public async Task<bool> IsAgentBusyAsync(CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct);
        try{await UiAsync("return true;",ct);return activeAgentRequests.Count>0;}
        finally{_gate.Release();}
    }
    public async Task ObserveActionAsync(CancellationToken ct=default)
    {await _gate.WaitAsync(ct);try{await UiAsync("return true;",ct);if(actionFailure!=null)throw new FlowBrowserRuntimeException(actionFailure);}finally{_gate.Release();}}
    public async Task SaveProofAsync(string directory,CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if(_page==null)return;
            var state=await UiAsync("if(location.origin!=='https://flow.google.com')return null;return {path:location.pathname,alerts:[...document.querySelectorAll('[role=alert],mat-snack-bar-container')].map(e=>e.textContent.trim()).slice(-8)};",ct);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory,"service-state.json"),JsonSerializer.Serialize(new{state,actionAccepted,actionFailure,requests=actionRequests.Values.ToArray(),rpcIds=observedRpcIds.ToArray()}),ct);
            if(state.ValueKind==JsonValueKind.Null)return;
            var screen=await _page.InvokeAsync("Page.captureScreenshot",new{format="png",captureBeyondViewport=false},ct);await File.WriteAllBytesAsync(Path.Combine(directory,"service-flow.png"),Convert.FromBase64String(screen.GetProperty("data").GetString()!),ct);
        }
        finally{_gate.Release();}
    }

    // Deliberately narrower than the supplied userscript: only the country field.
    // Never changes age, authentication, logout, CAPTCHA, or general availability.
    internal const string CountryCompatibilityScript = """
    (()=>{if(location.origin!=='https://flow.google.com')return;
    const patch=raw=>{if(typeof raw!=='string'||!raw.includes('cPZSdc'))return raw;
      try{const data=JSON.parse(raw);let changed=false;
        const visit=(item,depth=0)=>{if(!Array.isArray(item)||depth>12)return;
          if(item[0]==='wrb.fr'&&item[1]==='cPZSdc'&&typeof item[2]==='string'){
            const value=JSON.parse(item[2]);if(!Array.isArray(value))return;
            while(value.length<31)value.push(null);value[30]=true;item[2]=JSON.stringify(value);changed=true;
          }else for(const child of item)visit(child,depth+1)};
        visit(data);if(changed){window.__flowStudioCountryPatched=(window.__flowStudioCountryPatched||0)+1;return JSON.stringify(data)}}catch{}return raw};
    const proto=XMLHttpRequest.prototype;
    // Keep Google's length-framed stream valid after changing a JSON field.
    const stream=raw=>{if(typeof raw!=='string'||!raw.includes('cPZSdc'))return raw;
      const start=raw.indexOf('\n\n');if(start<0)return patch(raw);
      let out=raw.slice(0,start+2),pos=start+2;
      while(pos<raw.length){const nl=raw.indexOf('\n',pos);if(nl<0)return out+raw.slice(pos);
        const len=Number(raw.slice(pos,nl));if(!Number.isInteger(len)||len<1||nl+len>raw.length)return raw;
        const old=raw.slice(nl+1,nl+len),next=patch(old);out+=(next.length+1)+'\n'+next;pos=nl+len}return out};
    for(const key of ['responseText','response']){const d=Object.getOwnPropertyDescriptor(proto,key);if(!d?.get)continue;
      Object.defineProperty(proto,key,{...d,get(){const value=d.get.call(this);return typeof value==='string'?stream(value):value}})}
    const originalFetch=window.fetch;
    window.fetch=async function(...args){const response=await originalFetch.apply(this,args);
      try{const address=new URL(typeof args[0]==='string'?args[0]:args[0].url,location.href);
        if(address.origin!==location.origin||!address.pathname.endsWith('/data/batchexecute'))return response;
        const raw=await response.clone().text(),next=stream(raw);if(next===raw)return response;
        const headers=new Headers(response.headers);headers.delete('content-length');
        return new Response(next,{status:response.status,statusText:response.statusText,headers})}catch{return response}};
    })();
    """;

    async Task<JsonElement> UiAsync(string expression,CancellationToken ct)
    {
        await ProcessRegionResponsesAsync(ct);
        var result=await _page!.InvokeAsync("Runtime.evaluate",new{expression="JSON.stringify((()=>{if(location.origin!=='https://flow.google.com')return {flowStudioForeignOrigin:true};"+expression+"})())",returnByValue=true,awaitPromise=false,userGesture=false,silent=true},ct);
        await ProcessRegionResponsesAsync(ct);
        using var json=ReadEvaluationJson(result);if(json.RootElement.ValueKind==JsonValueKind.Object&&json.RootElement.TryGetProperty("flowStudioForeignOrigin",out _))throw new FlowBrowserRuntimeException("Служебное окно перешло с Flow на другую страницу. Завершите вход или проверку Google вручную.");return json.RootElement.Clone();
    }
    async Task ProcessRegionResponsesAsync(CancellationToken ct)
    {
        while(regionResponses.TryDequeue(out var response))
        {
            try
            {
            var id=response.GetProperty("requestId").GetString()!;
            var url=response.GetProperty("request").GetProperty("url").GetString();
            var rpcIds=Uri.TryCreate(url,UriKind.Absolute,out var actionUri)?actionUri.Query.TrimStart('?').Split('&').Where(p=>p.StartsWith("rpcids=",StringComparison.Ordinal)).SelectMany(p=>Uri.UnescapeDataString(p[7..]).Split(',')).ToArray():[];
            var actionRpc=rpcIds.FirstOrDefault(GenerationRpcIds.Contains);
            if(actionRpc!=null&&response.TryGetProperty("responseStatusCode",out var actionStatus))
            {
                if(actionStatus.GetInt32()>=400)actionFailure="Google отклонил генерацию: HTTP "+actionStatus.GetInt32()+". Повтора не было.";
                else
                {
                    var actionBody=await _page!.InvokeAsync("Fetch.getResponseBody",new{requestId=id},ct);var rawAction=actionBody.GetProperty("body").GetString()??"";
                    if(actionBody.TryGetProperty("base64Encoded",out var b64)&&b64.GetBoolean())rawAction=Encoding.UTF8.GetString(Convert.FromBase64String(rawAction));
                    try{var result=FlowCookieAuthHandler.DecodeBatchResponse(rawAction,actionRpc);if(result.ErrorCode is {} code)actionFailure="Google отклонил генерацию: код "+code+(result.FailureReason==null?"":" ("+result.FailureReason+")")+". Повтора не было.";else actionAccepted++;}catch(Exception ex)when(ex is JsonException or InvalidDataException){actionFailure="Ответ Google о приёме генерации не распознан. Проверьте медиатеку перед повтором.";}
                }
                await _page!.InvokeAsync("Fetch.continueRequest",new{requestId=id},ct);continue;
            }
            var target=Uri.TryCreate(url,UriKind.Absolute,out var endpoint)&&endpoint.Host=="flow.google.com"&&endpoint.Query.Split('&').Any(p=>p.Contains("rpcids=")&&Uri.UnescapeDataString(p[(p.IndexOf("rpcids=",StringComparison.Ordinal)+7)..]).Split(',').Contains("cPZSdc"));
            if(!EnableCountryCompatibility||!target||!response.TryGetProperty("responseStatusCode",out var status)||status.GetInt32()!=200)
            {await _page!.InvokeAsync("Fetch.continueRequest",new{requestId=id},ct);continue;}
            var body=await _page!.InvokeAsync("Fetch.getResponseBody",new{requestId=id},ct);
            var raw=body.GetProperty("body").GetString()??"";
            if(body.TryGetProperty("base64Encoded",out var encoded)&&encoded.GetBoolean())raw=Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            var patched=PatchCountryResponse(raw);
            if(patched==raw){await _page.InvokeAsync("Fetch.continueRequest",new{requestId=id},ct);continue;}
            var headers=response.GetProperty("responseHeaders").EnumerateArray().Select(h=>new{name=h.GetProperty("name").GetString()!,value=h.GetProperty("value").GetString()!}).Where(h=>!h.name.Equals("content-length",StringComparison.OrdinalIgnoreCase)&&!h.name.Equals("content-encoding",StringComparison.OrdinalIgnoreCase)).ToArray();
            await _page.InvokeAsync("Fetch.fulfillRequest",new{requestId=id,responseCode=200,responseHeaders=headers,body=Convert.ToBase64String(Encoding.UTF8.GetBytes(patched))},ct);regionResponseChanges++;
            }
            catch(FlowRuntimeCdpException ex)when(ex.Message.Contains("Invalid InterceptionId",StringComparison.OrdinalIgnoreCase)||ex.Message.Contains("No resource",StringComparison.OrdinalIgnoreCase)||ex.Message.Contains("Invalid requestId",StringComparison.OrdinalIgnoreCase)){}
        }
    }
    internal static string PatchCountryResponse(string raw)
    {
        if(raw.Length>16*1024*1024||!raw.Contains("cPZSdc",StringComparison.Ordinal))return raw;
        bool Visit(JsonNode? node)
        {
            if(node is not JsonArray array)return false;
            if(array.Count>2&&array[0] is JsonValue first&&first.TryGetValue<string>(out var tag)&&tag=="wrb.fr"&&array[1]?.GetValue<string>()=="cPZSdc"&&array[2] is JsonValue encoded&&encoded.TryGetValue<string>(out var payload))
            {if(JsonNode.Parse(payload) is not JsonArray config)return false;while(config.Count<31)config.Add(null);config[30]=true;array[2]=config.ToJsonString();return true;}
            var changed=false;foreach(var child in array)changed=Visit(child)||changed;return changed;
        }
        try
        {
            var start=raw.StartsWith(")]}'",StringComparison.Ordinal)?raw.IndexOf('\n')+1:0;
            while(start<raw.Length&&char.IsWhiteSpace(raw[start]))start++;
            if(start>=raw.Length)return raw;
            if(raw[start]=='['){var json=JsonNode.Parse(raw[start..]);return Visit(json)?raw[..start]+json!.ToJsonString():raw;}
            var output=new StringBuilder(raw[..start]);var position=start;var changed=false;
            while(position<raw.Length)
            {
                if(char.IsWhiteSpace(raw[position])){output.Append(raw[position++]);continue;}
                var nl=raw.IndexOf('\n',position);if(nl<0||!int.TryParse(raw.AsSpan(position,nl-position),NumberStyles.None,CultureInfo.InvariantCulture,out var length)||length<1||nl+length>raw.Length)return raw;
                var text=raw.Substring(nl+1,length-1);var json=JsonNode.Parse(text);var updated=Visit(json);changed|=updated;
                var next=updated?json!.ToJsonString():text;output.Append(next.Length+1).Append('\n').Append(next);position=nl+length;
            }
            return changed?output.ToString():raw;
        }
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException){return raw;}
    }
    const string Visible="const visible=e=>!!e&&e.getBoundingClientRect().width>0&&e.getBoundingClientRect().height>0;";
    async Task WaitUiAsync(string condition,CancellationToken ct,int seconds=30)
    {
        var end=DateTime.UtcNow.AddSeconds(seconds);
        while(DateTime.UtcNow<end)
        {ct.ThrowIfCancellationRequested();if((await UiAsync("return !!("+condition+");",ct)).ValueKind==JsonValueKind.True)return;await Task.Delay(250,ct);}
        var diagnosis=await UiAsync("return {path:location.pathname,countryPatched:Number(window.__flowStudioCountryPatched||0)};",ct);
        throw new FlowBrowserRuntimeException("Интерфейс Flow не готов: "+diagnosis.GetRawText()+"; config responses="+regionResponseChanges+"; RPC="+string.Join(',',observedRpcIds)+". Запрос не отправлен повторно.");
    }
    async Task ClickUiAsync(string selector,string? text,CancellationToken ct,bool contains=false)
    {
        if(text=="Триггер настроек"){selector="button.settings-trigger-button";text=null;}
        if(text=="Начать генерацию"){selector="button.generate-icon-button";text=null;}
        var requested=text??selector;
        var aliases=text switch{"Видео"=>new[]{"Видео","Video"},"Развернуть"=>new[]{"Развернуть","Expand"},"Открыть историю сеансов"=>new[]{"Открыть историю сеансов","Open session history"},"Начать новый сеанс"=>new[]{"Начать новый сеанс","Start new session"},_=>new[]{text??""}};
        if(text?.EndsWith(" сек.")==true)aliases=[text,text.Replace(" сек."," sec."),text.Replace(" сек.","s"),text.Replace(" сек."," sec")];
        var match=text==null?"true":"("+JsonSerializer.Serialize(aliases)+").some(t=>e.getAttribute('aria-label')===t||e.textContent.trim().replace(/\\s+/g,' ')"+(contains?".includes(t)":"===t")+")";
        var probe=Visible+"const found=[...document.querySelectorAll("+JsonSerializer.Serialize(selector)+")].filter(e=>visible(e)&&"+match+");if(found.length!==1||found[0].disabled||found[0].getAttribute('aria-disabled')==='true')return null;const r=found[0].getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2;const hit=document.elementFromPoint(x,y);if(!hit||!(found[0]===hit||found[0].contains(hit)))return null;return {x,y};";
        var result=await UiAsync(probe,ct);var until=DateTime.UtcNow.AddSeconds(8);
        while(result.ValueKind==JsonValueKind.Null&&DateTime.UtcNow<until){await Task.Delay(300,ct);result=await UiAsync(probe,ct);}
        if(result.ValueKind!=JsonValueKind.Object)
        {var labels=await UiAsync(Visible+"return [...document.querySelectorAll('button')].filter(visible).map(e=>({label:(e.getAttribute('aria-label')||'').replace(/[\\w.+-]+@[\\w.-]+/g,'[account]'),text:e.textContent.trim().slice(0,90),css:e.className})).slice(-35);",ct);throw new FlowBrowserRuntimeException("Не найден единственный доступный элемент Flow: "+requested+". Платное действие не повторяется. Элементы: "+labels.GetRawText());}
        var x=result.GetProperty("x").GetDouble();var y=result.GetProperty("y").GetDouble();
        await _page!.InvokeAsync("Input.dispatchMouseEvent",new{type="mousePressed",x,y,button="left",clickCount=1},ct);
        await _page.InvokeAsync("Input.dispatchMouseEvent",new{type="mouseReleased",x,y,button="left",clickCount=1},ct);
        await Task.Delay(200,ct);
    }
    async Task OpenProjectUiAsync(string projectId,CancellationToken ct,bool keepPage=false)
    {
        projectId=FlowCloudLibraryService.NormalizeProjectId(projectId);
        _=await EnsureReadyCoreAsync(ct);
        if(!responseInterceptionInstalled){await _page!.InvokeAsync("Fetch.enable",new{patterns=new[]{new{urlPattern="https://flow.google.com/*/data/batchexecute*",requestStage="Response"}}},ct);responseInterceptionInstalled=true;}
        if(EnableCountryCompatibility&&!compatibilityInstalled)
        {await _page!.InvokeAsync("Page.addScriptToEvaluateOnNewDocument",new{source=CountryCompatibilityScript},ct);compatibilityInstalled=true;preparedProject=null;}
        if(!keepPage||preparedProject!=projectId)
        {
            await _page!.InvokeAsync("Page.navigate",new{url="https://flow.google.com/project/"+projectId+"?hl=ru"},ct);preparedProject=projectId;
        }
        await WaitUiAsync("document.querySelector('[contenteditable=true]')",ct,45);
        await Task.Delay(1800,ct);
        var origin=await UiAsync("return location.origin==='https://flow.google.com'&&location.pathname==="+JsonSerializer.Serialize("/project/"+projectId)+";",ct);
        if(origin.ValueKind!=JsonValueKind.True)throw new FlowBrowserRuntimeException("Flow открыл другую страницу. Запрос не отправлен.");
    }
    async Task SetPromptUiAsync(string prompt,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(prompt)||prompt.Length>30000)throw new ArgumentException("Некорректный запрос Flow.");
        var focused=await UiAsync(Visible+"const fields=[...document.querySelectorAll('[contenteditable=true]')].filter(visible);if(fields.length!==1)return false;const e=fields[0];e.focus();const range=document.createRange();range.selectNodeContents(e);const sel=getSelection();sel.removeAllRanges();sel.addRange(range);return true;",ct);
        if(focused.ValueKind!=JsonValueKind.True)throw new FlowBrowserRuntimeException("Не найдено поле запроса Flow.");
        await _page!.InvokeAsync("Input.insertText",new{text=prompt},ct);
        await WaitUiAsync("[...document.querySelectorAll('[contenteditable=true]')].some(e=>e.textContent.trim()==="+JsonSerializer.Serialize(prompt.Trim())+")",ct,5);
    }
    public async Task SubmitVideoUiAsync(FlowGenerationRequest request,FlowCatalogModel model,CancellationToken ct=default)
    {
        if(request.Kind!="video"||request.Images?.Count>0||request.FirstFrame!=null||request.LastFrame!=null||request.Seed!=null||
            request.AspectRatio is not("16:9" or "9:16")||request.Count is <1 or >4||model.DurationSeconds is not(4 or 6 or 8 or 10)||
            model.DisplayName!="Omni 1.1 Flash")
            throw new FlowBrowserRuntimeException("Служебный интерфейс сейчас поддерживает текст → видео Omni 1.1 Flash, 4–10 секунд, 360p/720p. Для других режимов выберите прямое подключение.");
        await _gate.WaitAsync(ct);
        try
        {
            await OpenProjectUiAsync(request.ProjectId,ct);
            var expandedAgent=await UiAsync("return !!document.querySelector('[aria-label=\"Открыть историю сеансов\"]');",ct);
            if(expandedAgent.ValueKind==JsonValueKind.True)await ClickUiAsync("button","Закрыть",ct);
            await WaitUiAsync("document.querySelector('button.agent-mode-chip')",ct,10);
            var active=await UiAsync("return document.querySelector('button.agent-mode-chip')?.getAttribute('aria-pressed')==='true';",ct);
            if(active.ValueKind==JsonValueKind.True)await ClickUiAsync("button.agent-mode-chip",null,ct);
            await WaitUiAsync("document.querySelector('button.agent-mode-chip')?.getAttribute('aria-pressed')==='false'&&document.querySelector('button.settings-trigger-button')",ct,10);
            await ClickUiAsync("button","Триггер настроек",ct);
            await ClickUiAsync("button[role=radio]","Видео",ct,true);
            var currentFamily=await UiAsync(Visible+"return [...document.querySelectorAll('button')].some(e=>visible(e)&&e.textContent.includes('Omni 1.1 Flash'));",ct);
            if(currentFamily.ValueKind!=JsonValueKind.True)
            {await ClickUiAsync("button","Выбрать семейство моделей",ct);await ClickUiAsync("[role=menuitem]","Omni 1.1 Flash",ct,true);}
            await ClickUiAsync("button[role=radio]",request.AspectRatio,ct,true);
            await ClickUiAsync("button[role=radio]",request.OutputResolution==4?"360p":"720p",ct,true);
            await ClickUiAsync("button[role=radio]",model.DurationSeconds+" сек.",ct);
            await ClickUiAsync("button[role=radio]","x"+request.Count,ct);
            var verified=await UiAsync("const radios=[...document.querySelectorAll('button[role=radio][aria-checked=true]')].map(e=>e.textContent.trim());return "+
                "radios.some(t=>t.includes("+JsonSerializer.Serialize(request.AspectRatio)+"))&&radios.some(t=>t.startsWith("+JsonSerializer.Serialize(request.OutputResolution==4?"360p":"720p")+"))&&radios.some(t=>/^("+model.DurationSeconds+")(?:\\s*(?:сек\\.|sec\\.?|s))$/.test(t))&&radios.includes("+JsonSerializer.Serialize("x"+request.Count)+");",ct);
            if(verified.ValueKind!=JsonValueKind.True)throw new FlowBrowserRuntimeException("Параметры Flow не совпали с нативным запросом. Генерация не отправлена.");
            await _page!.InvokeAsync("Input.dispatchKeyEvent",new{type="keyDown",key="Escape",code="Escape",windowsVirtualKeyCode=27},ct);
            await _page.InvokeAsync("Input.dispatchKeyEvent",new{type="keyUp",key="Escape",code="Escape",windowsVirtualKeyCode=27},ct);
            await SetPromptUiAsync(request.Prompt,ct);
            actionRequests.Clear();actionFailure=null;actionAccepted=0;
            await ClickUiAsync("button","Начать генерацию",ct);
            var sentDeadline=DateTime.UtcNow.AddSeconds(35);
            while(DateTime.UtcNow<sentDeadline&&actionAccepted==0&&actionFailure==null){await Task.Delay(250,ct);await UiAsync("return true;",ct);}
            if(actionFailure!=null)throw new FlowBrowserRuntimeException(actionFailure);
            if(actionAccepted==0)throw new FlowBrowserRuntimeException("Flow не подтвердил приём запроса. Проверьте служебное окно и медиатеку перед повтором.");
        }
        finally{_gate.Release();}
    }

    public async Task SubmitAgentUiAsync(string projectId,string prompt,string? existingSessionTitle=null,CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await OpenProjectUiAsync(projectId,ct,keepPage:true);
            var expanded=await UiAsync("return !!document.querySelector('[aria-label=\"Открыть историю сеансов\"]');",ct);
            if(expanded.ValueKind!=JsonValueKind.True)
            {
                var active=await UiAsync("return document.querySelector('button.agent-mode-chip')?.getAttribute('aria-pressed')==='true';",ct);
                if(active.ValueKind!=JsonValueKind.True)await ClickUiAsync("button.agent-mode-chip",null,ct);
                await ClickUiAsync("button","Развернуть",ct);
            }
            if(existingSessionTitle!=null)
            {
                await ClickUiAsync("button","Открыть историю сеансов",ct);
                await ClickUiAsync("button",existingSessionTitle,ct);
            }
            else await ClickUiAsync("button","Начать новый сеанс",ct);
            await SetPromptUiAsync(prompt,ct);
            actionRequests.Clear();actionFailure=null;actionAccepted=0;
            await ClickUiAsync("button","Начать генерацию",ct);
        }
        finally{_gate.Release();}
    }
}
