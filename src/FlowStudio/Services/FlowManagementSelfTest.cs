using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

/// <summary>Synthetic native protocol fixtures only: no account writes, network, deletion or credit use.</summary>
internal static class FlowManagementSelfTest
{
    public static async Task<string> RunAsync(CancellationToken ct=default)
    {
        const string project="00000000-0000-0000-0000-000000000001";
        const string workflow="workflow-fixture",collection="collection-fixture",credential=FlowCookieAuthHandler.SessionCredentialMarker;
        var expected=new Queue<(string Rpc,Action<JsonElement> Check,Func<JsonElement,object?> Response)>();
        var sent=0;
        using var adapter=new FlowCookieAuthHandler(new FixtureHandler(async(request,token)=>
        {
            sent++;
            Require(expected.Count>0,"Unexpected management request or automatic retry.");
            var fixture=expected.Dequeue();
            Require(request.RequestUri?.Host=="flow.google.com"&&request.RequestUri.Query.Contains("rpcids="+fixture.Rpc),"Wrong native management route/RPC mapping.");
            Require(request.Headers.Authorization is null,"Management sent session marker as a real bearer token.");
            var form=(await request.Content!.ReadAsStringAsync(token)).Split('&').Select(p=>p.Split('=',2)).ToDictionary(p=>Uri.UnescapeDataString(p[0].Replace('+',' ')),p=>Uri.UnescapeDataString(p[1].Replace('+',' ')));
            using var envelope=JsonDocument.Parse(form["f.req"]);
            using var body=JsonDocument.Parse(envelope.RootElement[0][0][1].GetString()!);
            fixture.Check(body.RootElement);
            var payload=JsonSerializer.Serialize(fixture.Response(body.RootElement));
            var framed=JsonSerializer.Serialize(new object?[]{new object?[]{"wrb.fr",fixture.Rpc,payload,null,null,null,"generic"}});
            var block="\n"+framed+"\n";
            return new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(")]}'\n\n"+block.Length+block,Encoding.UTF8)};
        })){Session=new FlowBrowserSession([new("SID","synthetic-management-cookie",".google.com","/",null,true,true,"None")],new Dictionary<string,string?>{["SNlM0e"]="synthetic-management-xsrf"},"ManagementFixture",DateTimeOffset.UtcNow)};
        using var api=new HttpClient(adapter);
        var service=new FlowManagementService(api);

        Expect("o8DA4",b=>{Require(Text(b,1)=="projects/"+project&&Text(Field(b,2),1)=="Changed title","Project rename target/title wrong.");Mask(b,3,"project_title");Require(Int(Field(b,4),2)==22,"Project client context missing.");});
        await service.RenameProjectAsync(project," Changed title ",credential,ct);
        Expect("o8DA4",b=>{Require(Text(Field(b,2),2)=="media-fixture"&&Bool(Field(b,2),3),"Project cover key/user flag wrong.");Mask(b,3,"thumbnail_media_key","is_thumbnail_set_by_user");});
        await service.SetProjectCoverAsync(project,"media-fixture",credential,ct);
        Expect("o8DA4",b=>Require(Text(Field(b,2),2)==""&&!Bool(Field(b,2),3),"Reset project cover did not clear key/flag."));
        await service.SetProjectCoverAsync(project,null,credential,ct);
        ExpectWorkflow("metadata.display_name",m=>Require(Text(m,1)=="Clip","Media title missing."));
        await service.RenameMediaAsync(project,workflow,"Clip",credential,ct);
        ExpectWorkflow("metadata.favorited",m=>Require(Bool(m,4),"Favorite field wrong."));
        await service.SetFavoriteAsync(project,workflow,true,credential,ct);
        ExpectWorkflow("metadata.favorited",m=>Require(Field(m,4).ValueKind==JsonValueKind.False,"Unfavorite omitted explicit false."));
        await service.SetFavoriteAsync(project,workflow,false,credential,ct);
        ExpectWorkflow("metadata.archived",m=>Require(Bool(m,3),"Trash field wrong."));
        await service.SetMediaTrashedAsync(project,workflow,true,credential,ct);
        ExpectWorkflow("metadata.archived",m=>Require(Field(m,3).ValueKind==JsonValueKind.False,"Restore omitted explicit false."));
        await service.SetMediaTrashedAsync(project,workflow,false,credential,ct);

        var collectionFixture=Message((1,collection),(2,"parent-fixture"),(3,Message((1,"Collection"),(2,"media-fixture"),(3,true),(4,new object?[]{"1700000000",0}),(5,true),(6,new object?[]{"1700000100",0}))),(4,project));
        Expect("Zzl0ze",b=>Require(Text(b,1)=="projects/"+project,"Collection list target wrong."),_=>new object?[]{new object?[]{collectionFixture}});
        var collections=await service.ListCollectionsAsync(project,credential,ct);
        Require(collections.Count==1&&collections[0] is {Id:collection,ParentId:"parent-fixture",IsFavorite:true,IsTrashed:true,ThumbnailMediaName:"media-fixture"}&&collections[0].CreatedAt is not null&&collections[0].TrashedAt is not null,"Collection metadata parser lost fields.");
        Expect("Uxbujd",b=>{var item=Field(b,2);Require(Text(b,1)=="projects/"+project&&Guid.TryParse(Text(item,1),out _)&&Text(item,2)=="parent-fixture"&&Text(item,4)==project&&Text(Field(item,3),1)=="New collection","Create collection body wrong.");},b=>Field(b,2).Clone());
        Require((await service.CreateCollectionAsync(project,"New collection","parent-fixture",credential,ct)).Title=="New collection","Created collection was not parsed.");
        ExpectCollection("metadata.display_name",m=>Require(Text(m,1)=="Renamed","Collection rename wrong."));
        await service.RenameCollectionAsync(project,collection,"Renamed",credential,ct);
        ExpectCollection("metadata.favorited",m=>Require(Bool(m,5),"Collection favorite field wrong."));
        await service.SetCollectionFavoriteAsync(project,collection,true,credential,ct);
        ExpectCollection("metadata.archived",m=>Require(Field(m,3).ValueKind==JsonValueKind.False,"Collection restore missing false."));
        await service.SetCollectionTrashedAsync(project,collection,false,credential,ct);
        ExpectCollection("metadata.thumbnail_media_key",m=>Require(Text(m,2)=="media-fixture","Collection cover wrong."));
        await service.SetCollectionCoverAsync(project,collection,"media-fixture",credential,ct);
        Expect("O2COMe",b=>{Require(Text(Field(b,1),2)==""&&Text(Field(b,1),4)==project,"Move collection to root wrong.");Mask(b,2,"parent_collection_id");});
        await service.MoveCollectionAsync(project,collection,null,credential,ct);
        Expect("kVdhHf",b=>Require(Text(b,1)==project&&Text(Field(b,2),1)==collection&&Field(b,3).GetArrayLength()==1&&Field(b,6).ValueKind==JsonValueKind.Undefined,"Move to collection target/deduplication wrong."));
        await service.MoveMediaToCollectionAsync(project,[workflow,workflow],collection,credential,ct);
        Expect("kVdhHf",b=>Require(Field(b,2).ValueKind==JsonValueKind.Null&&Field(b,6).ValueKind==JsonValueKind.Array&&Field(b,6).GetArrayLength()==0,"Move to root destination oneof wrong."));
        await service.MoveMediaToCollectionAsync(project,[workflow],null,credential,ct);

        var flow=Message((1,workflow),(4,Message((1,"Trashed clip"),(3,true),(4,true))),(5,project));
        var media=Message((1,"media-fixture"),(2,project),(3,workflow),(6,Array.Empty<object?>()),(8,Message((1,Array.Empty<object?>()))));
        Expect("Zzl0ze",_=>{},_=>Message((2,new object?[]{flow}),(3,new object?[]{media})));
        var trash=await service.ListTrashedMediaAsync(project,credential,ct);
        Require(trash.Count==1&&trash[0].IsTrashed&&trash[0].IsFavorite&&trash[0].WorkflowId==workflow,"Native trash list lost workflow archive/favorite metadata.");
        Expect("cz8Z4b",b=>Require(Text(b,3)==project&&Field(b,1).ValueKind==JsonValueKind.Null&&Field(b,2)[0].GetString()==workflow,"Permanent workflow delete mixed asset types."));
        await service.PermanentlyDeleteMediaAsync(project,[workflow],credential,ct);
        Expect("cz8Z4b",b=>Require(Text(b,3)==project&&Field(b,1)[0].GetString()==collection&&Field(b,2).ValueKind==JsonValueKind.Null,"Permanent collection delete mixed asset types."));
        await service.PermanentlyDeleteCollectionsAsync(project,[collection],credential,ct);
        Expect("QI2zvc",b=>Require(Text(b,1)=="projects/"+project,"Permanent project delete target wrong."));
        await service.PermanentlyDeleteProjectAsync(project,credential,ct);
        Require(expected.Count==0,"A confirmed management request was not sent.");
        Expect("Zzl0ze",_=>{},_=>new object?[]{new object?[]{Message((1,collection),(4,"00000000-0000-0000-0000-000000000002"))}});
        try{await service.ListCollectionsAsync(project,credential,ct);throw new InvalidOperationException("Cross-project collection was accepted.");}
        catch(FlowCloudLibraryException){}
        var beforeInvalid=sent;
        await Reject(()=>service.RenameProjectAsync("invalid","Title",credential,ct));
        await Reject(()=>service.RenameMediaAsync(project,"","Title",credential,ct));
        await Reject(()=>service.MoveCollectionAsync(project,collection,collection,credential,ct));
        await Reject(()=>service.PermanentlyDeleteMediaAsync(project,[],credential,ct));
        await Reject(()=>service.RenameCollectionAsync(project,collection,"\n",credential,ct));
        Require(sent==beforeInvalid,"Invalid/destructive empty-target input reached transport.");
        var denialCalls=0;
        using var denialClient=new HttpClient(new FixtureHandler((request,token)=>
        {
            denialCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){RequestMessage=request,Content=new StringContent("[7,\"Synthetic private server text\"]",Encoding.UTF8,"application/json+protobuf")});
        }));
        try{await new FlowManagementService(denialClient).SetFavoriteAsync(project,workflow,true,credential,ct);throw new InvalidOperationException("Denied mutation was accepted.");}
        catch(FlowCloudLibraryException error){Require(error.HttpStatusCode==403&&error.CanonicalCode==7&&denialCalls==1&&!error.Message.Contains("private server"),"Denied management request lost canonical code, leaked server data or retried.");}
        return "Native project, workflow and collection management masks, RPC routing, favorite/trash metadata, root destinations, explicit deletion targets and invalid-input rejection passed with synthetic data only.";

        void Expect(string rpc,Action<JsonElement> check,Func<JsonElement,object?>? response=null)=>expected.Enqueue((rpc,check,response??(_=>Array.Empty<object?>())));
        void ExpectWorkflow(string path,Action<JsonElement> metadata)=>Expect("mYWVGd",b=>{var item=Field(b,1);Require(Text(item,1)==workflow&&Text(item,5)==project,"Workflow mutation escaped its project.");Mask(b,2,path);metadata(Field(item,4));});
        void ExpectCollection(string path,Action<JsonElement> metadata)=>Expect("O2COMe",b=>{var item=Field(b,1);Require(Text(item,1)==collection&&Text(item,4)==project,"Collection mutation escaped its project.");Mask(b,2,path);metadata(Field(item,3));});
    }

    static async Task Reject(Func<Task> action){try{await action();throw new InvalidOperationException("Invalid management input was accepted.");}catch(ArgumentException){}}
    static JsonElement Field(JsonElement value,int field)=>FlowNativeRpc.Field(value,field);
    static string? Text(JsonElement value,int field)=>FlowNativeRpc.Text(value,field);
    static int? Int(JsonElement value,int field)=>FlowNativeRpc.Integer(value,field);
    static bool Bool(JsonElement value,int field)=>FlowNativeRpc.Boolean(value,field);
    static void Mask(JsonElement request,int field,params string[] paths)=>Require(Field(Field(request,field),1).EnumerateArray().Select(p=>p.GetString()).SequenceEqual(paths),"Mutation field mask differed from the public source.");
    static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    static object?[] Message(params (int Field,object? Value)[] fields){var value=new object?[fields.Max(f=>f.Field)];foreach(var (field,item) in fields)value[field-1]=item;return value;}
    sealed class FixtureHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>action(request,cancellationToken);}
}
