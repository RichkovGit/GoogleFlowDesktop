using System.Net.Http;
using System.Text;
using System.Text.Json;
using static FlowStudio.Services.FlowCloudLibrarySelfTest;

namespace FlowStudio.Services;

internal static class FlowAgentSelfTest
{
    public static async Task<string> RunAsync(CancellationToken ct=default)
    {
        const string project="fc73dcf8-095c-44a3-b222-d4b13719118b";
        const string session="ec572cc7-1551-4611-b089-704974af42f5";
        var summary=new object?[]{session,new object?[]{"Actual Flow session",null,new object?[]{"1760000000",0}}};
        var textStruct=new object?[]{new object?[]{new object?[]{"text",new object?[]{null,null,"Ответ агента"}}}};
        var turn=Message((1,new object?[]{new object?[]{new object?[]{new object?[]{"Привет"}}}}),(2,new object?[]{new object?[]{textStruct}}));
        var conversation=FlowAgentService.ParseConversation(JsonSerializer.SerializeToElement(new object?[]{summary,new object?[]{turn}}));
        Require(conversation.Id==session&&conversation.Title=="Actual Flow session"&&conversation.Turns.Single().UserText=="Привет"&&conversation.Turns.Single().AssistantText=="Ответ агента","Legacy session history parser failed");
        var modernTurn=Message((1,new object?[]{new object?[]{new object?[]{new object?[]{"Make a scene"}}}}),(3,new object?[]{Message((10,new object?[]{"Современный ответ",false,1})),Message((15,new object?[]{"approval-event",null,20}))}));
        var modern=FlowAgentService.ParseConversation(JsonSerializer.SerializeToElement(new object?[]{summary,new object?[]{modernTurn}}));
        Require(modern.Turns.Single().AssistantText=="Современный ответ"&&modern.Turns.Single().RequiresApproval&&!modern.Turns.Single().CanApprove&&modern.Turns.Single().CreditCost==20,"Modern history or unsupported bidi approval misrepresented");
        var settings=FlowAgentService.ParseSettings(JsonSerializer.SerializeToElement(Message((2,new object?[]{true,new object?[]{new object?[]{"instruction-1","Hero","Same character",true,new object?[]{"media-1"}}}}),(3,new object?[]{new object?[]{null,3,4,"image-key"},new object?[]{"video-key",2,1}}),(4,2))));
        Require(settings.ApprovalPolicy==2&&settings.Instructions.Single().Description=="Same character"&&settings.Instructions.Single().ImageMediaIds!.Single()=="media-1"&&settings.ImageDefaults?.Count==4&&settings.VideoDefaults?.ModelKey=="video-key","Agent settings parser mismatch");
        var body="\n[[\"wrb.fr\",null,\"[\\\"кадр\\\"]\"]]\n";
        var framed=")]}'\n\n"+body.Length+body;
        var pending=new StringBuilder();var started=false;var frames=new List<string>();
        foreach(var c in framed){pending.Append(c);frames.AddRange(FlowAgentService.TakeFrames(pending,ref started));}
        Require(frames.Count==1&&pending.Length==0,"Fragmented Unicode stream framing failed");
        Require(FlowAgentService.MergeText("кадр","кадр готов")=="кадр готов"&&FlowAgentService.MergeText("кадр готов","готов")=="кадр готов","Cumulative stream text duplicated");
        var updateCalls=0;
        using var api=new HttpClient(new Fixture(async request=>
        {
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var root=json.RootElement;
            Require(request.RequestUri!.AbsolutePath.EndsWith(".AiSandbox/UpdateAgentInfo",StringComparison.Ordinal),"Wrong agent settings RPC");
            Require(root[0].GetString()=="projects/"+project,"Wrong agent settings project");
            Require(root[2][0].GetArrayLength()==1,"Settings update lost field mask");updateCalls++;return Json(Array.Empty<object>());
        }));
        using var adapter=new FlowCookieAuthHandler(new Fixture(_=>throw new InvalidOperationException("No real transport allowed")));
        using var agent=new FlowAgentService(api,adapter);
        await agent.UpdateApprovalPolicyAsync(project,1,"synthetic",ct);
        await agent.UpdateInstructionsAsync(project,[new("instruction-1","Title","Description",true)],"synthetic",ct);
        await agent.UpdateDefaultsAsync(project,"video",new("video-key",2,1),"synthetic",ct);
        Require(updateCalls==3,"Masked agent settings writes missing");
        try{await agent.UpdateInstructionsAsync(project,Enumerable.Range(1,4).Select(i=>new FlowAgentInstruction(i.ToString(),"","",true)).ToArray(),"synthetic",ct);throw new InvalidOperationException("Four enabled instructions accepted");}catch(ArgumentException){}
        var streamCalls=0;var progress=new InlineProgress();
        using var historyApi=new HttpClient(new Fixture(_=>Task.FromResult(Json(new object?[]{summary,new object?[]{turn}}))));
        adapter.Session=new FlowBrowserSession([new("SID","synthetic-cookie",".google.com","/",null,true,true,null)],new Dictionary<string,string?>{["SNlM0e"]="synthetic-xsrf",["FdrFJe"]="synthetic-fsid"},"FlowStudio fixture",DateTimeOffset.UtcNow);
        using var transport=new HttpClient(new Fixture(async request=>
        {
            streamCalls++;
            Require(request.RequestUri!.Host=="flow.google.com"&&request.RequestUri.AbsolutePath.EndsWith("/FlowCreationAgentService/StreamChat",StringComparison.Ordinal)==false&&request.RequestUri.AbsolutePath.EndsWith(".FlowCreationAgentService/StreamChat",StringComparison.Ordinal),"Wrong native agent stream endpoint");
            Require(request.Headers.Authorization is null&&request.Headers.GetValues("Cookie").Single()=="SID=synthetic-cookie","Stream authorization routing failed");
            var form=(await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(s=>s.Split('=',2)).ToDictionary(p=>Uri.UnescapeDataString(p[0]),p=>Uri.UnescapeDataString(p[1].Replace('+',' ')));
            using var wrapper=JsonDocument.Parse(form["f.req"]);using var message=JsonDocument.Parse(wrapper.RootElement[1].GetString()!);
            Require(message.RootElement[0].GetString()==session&&message.RootElement[1][0][0][0][0].GetString()=="Привет"&&message.RootElement[3][0].GetString()=="projects/"+project&&message.RootElement[3][5].GetInt32()==1,"Agent request protobuf fields moved");
            var payload=JsonSerializer.Serialize(new object?[]{new object?[]{textStruct}});
            var frame="\n"+JsonSerializer.Serialize(new object?[]{new object?[]{"wrb.fr",null,payload}})+"\n";
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent(")]}'\n\n"+frame.Length+frame)};
        }));
        using var streamed=new FlowAgentService(historyApi,adapter,transport);
        var streamedResult=await streamed.SendAsync(project,session,"Привет",1,FlowCookieAuthHandler.SessionCredentialMarker,progress,ct);
        Require(streamCalls==1&&progress.Text=="Ответ агента"&&streamedResult.Turns.Count==1,"Native agent stream did not report response exactly once");
        using var refusedTransport=new HttpClient(new Fixture(request=>
        {
            var error="\n"+JsonSerializer.Serialize(new object?[]{new object?[]{"wrb.fr",null,null,null,null,new object?[]{7,"synthetic-not-a-real-cookie-secret"}}})+"\n";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(")]}'\n\n"+error.Length+error)});
        }));
        using var refused=new FlowAgentService(historyApi,adapter,refusedTransport);
        try{await refused.SendAsync(project,session,"Synthetic",1,FlowCookieAuthHandler.SessionCredentialMarker,ct:ct);throw new InvalidOperationException("Server refusal accepted");}
        catch(FlowCloudLibraryException e){Require(e.CanonicalCode==7&&e.HttpStatusCode==403&&e.FailureReason is null&&!e.Message.Contains("synthetic-not-a-real-cookie-secret",StringComparison.Ordinal),"Canonical7 lost or unspecified failure invented a reason/leaked data");}
        return "Real agent session/history schemas, modern and legacy responses, approval capability, saved instructions/defaults/policy field masks, fragmented UTF-16 stream and cumulative text passed.";
    }
    sealed class InlineProgress:IProgress<string>{public string Text {get;private set;}="";public void Report(string value)=>Text=value;}
}
