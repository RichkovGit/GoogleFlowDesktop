using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

/// <summary>Reference/upload wire and mode-dispatch checks with synthetic HTTP only.</summary>
public static class FlowReferenceGenerationSelfTest
{
    const string Project="60eb88ad-40ac-4b64-96dc-5470f7a1c57d";
    const string Credential="reference-selftest-not-a-real-token";
    public static async Task<string> RunAsync(CancellationToken ct=default)
    {
        var basic=new FlowGenerationRequest(Project,"model-text","A quiet scene","video","16:9",1);
        var start=new FlowImageReference("media/start-ref",new(0.1,0.2,0.9,0.8));
        var end=new FlowImageReference("media/end-ref");
        var p=Json(FlowGenerationService.BuildVideoRequest(basic with{OutputResolution=4}));
        Require(p[0][0][7][0].GetInt32()==4,"360p must be submitted in the text resolution message, not inferred from a key.");
        p=Json(FlowGenerationService.BuildVideoRequest(basic with{OutputResolution=1}));
        Require(p[0][0].GetArrayLength()==5,"720p default should omit the optional resolution message.");
        var first=basic with{ModelKey="model-start",FirstFrame=start,OutputResolution=4,Count=2};
        p=Json(FlowGenerationService.BuildVideoReferenceRequest(first));
        Require(p[0].GetArrayLength()==2 && p[0][0][1].GetString()=="model-start" && p[0][0][2].GetInt32()==2,"Start-frame model/aspect/count fields changed.");
        Require(p[0][0][4][1].GetString()=="start-ref" && p[0][0][4][5][0].GetDouble()==0.1 && p[0][0][9][0].GetInt32()==4,"Start-frame media/crop/resolution fields changed.");
        Require(Guid.TryParse(p[0][0][5][4].GetString(),out _) && Guid.TryParse(p[0][0][5][5].GetString(),out _),"Start-frame metadata seeds missing.");
        var both=basic with{FirstFrame=start,LastFrame=end};
        p=Json(FlowGenerationService.BuildVideoReferenceRequest(both));
        Require(p[0][0][4][1].GetString()=="start-ref" && p[0][0][5][1].GetString()=="end-ref" && p[0][0][6].ValueKind==JsonValueKind.Array,"Start/end reference and metadata fields changed.");
        var ingredients=basic with{Images=new[]{start,end},OutputResolution=4};
        p=Json(FlowGenerationService.BuildVideoReferenceRequest(ingredients));
        Require(p[0][0][1].GetArrayLength()==2 && p[0][0][2].GetString()=="model-text" && p[0][0][3].GetInt32()==2 && p[0][0][11][0].GetInt32()==4,"Ingredients fields changed.");
        var image=new FlowGenerationRequest(Project,"image-model","Edit this image",Images:new[]{end},BaseImage:new("media/base"));
        p=Json(FlowGenerationService.BuildImageRequest(image));
        Require(p[1][0][2][0][0].GetString()=="base" && p[1][0][2][0][4].GetInt32()==2 && p[1][0][2][1][4].GetInt32()==1,"Image base role2 and reference role1 changed.");
        Reject(()=>FlowGenerationService.BuildVideoRequest(basic with{LastFrame=end}),"Last frame without first frame must fail before HTTP.");
        Reject(()=>FlowGenerationService.BuildVideoRequest(basic with{FirstFrame=start,Images=new[]{end}}),"Mixing incompatible modes must fail.");
        Reject(()=>FlowGenerationService.BuildVideoRequest(basic with{FirstFrame=new("media/x",new(0,0,2,1))}),"Invalid crop must fail.");
        p=Json(FlowGenerationService.BuildVideoRequest(both with{ModelKey="model-start-end-360p",OutputResolution=4}));
        Require(p[0][0].GetArrayLength()==7 && p[0][0][1].GetString()=="model-start-end-360p","Start/end should retain the selected usage key without inventing a resolution field.");
        Reject(()=>FlowGenerationService.BuildVideoRequest(basic with{Images=new[]{new FlowImageReference("https://outside.example/ref")}}),"Reference URL must not become a media ID.");

        var root=Path.Combine(Path.GetTempPath(),"FlowStudio-reference-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await TestUploadAsync(root,ct).ConfigureAwait(false);
            foreach(var test in new[]{(first,"BatchAsyncGenerateVideoStartImage"),(both,"BatchAsyncGenerateVideoStartAndEndImage"),(ingredients,"BatchAsyncGenerateVideoReferenceImages")})
                await TestDispatchAsync(test.Item1,test.Item2,root,ct).ConfigureAwait(false);
        }
        finally{try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
        return "Native image bytes upload and validation; exact image/base reference roles; first/last/ingredients fields/crops/resolution; mode dispatch and credential-free Google media download. Synthetic HTTP only.";
    }

    static async Task TestUploadAsync(string root,CancellationToken ct)
    {
        var path=Path.Combine(root,"пример.png");
        var png=new byte[]{137,80,78,71,13,10,26,10,0,0,0,0};
        await File.WriteAllBytesAsync(path,png,ct).ConfigureAwait(false);
        var requests=0;
        using var handler=new MockHandler(async(request,token)=>
        {
            requests++;
            Require(request.RequestUri!.AbsolutePath.EndsWith("/UploadImage",StringComparison.Ordinal),"Upload must use Flow UploadImage.");
            Require(request.Headers.Authorization?.Parameter==Credential,"Upload missing account credentials.");
            using var document=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false));
            var body=document.RootElement;
            Require(body[0][5].GetString()==Project && body[1].GetString()==Convert.ToBase64String(png),"Upload project or raw-byte base64 field changed.");
            Require(body[2].GetString()=="image/png" && body[3].GetBoolean() && !body[7].GetBoolean() && body[8].GetString()=="пример.png","Upload MIME/visibility/filename fields changed.");
            return Response(new object?[]{new object?[]{"media/uploaded",null,"workflow-one"}});
        });
        using var api=new HttpClient(handler);
        using var downloads=new HttpClient(new MockHandler((_,_)=>throw new InvalidOperationException("Upload must not fetch a media URL.")));
        var service=new FlowGenerationService(api,downloads);
        var uploaded=await service.UploadImageAsync(Project,path,Credential,cancellationToken:ct).ConfigureAwait(false);
        Require(uploaded.MediaName=="media/uploaded" && uploaded.MimeType=="image/png" && requests==1,"Upload response was not decoded.");
        await File.WriteAllTextAsync(path,"this is not a PNG",ct).ConfigureAwait(false);
        try{await service.UploadImageAsync(Project,path,Credential,cancellationToken:ct);throw new InvalidOperationException("Spoofed extension must fail.");}catch(ArgumentException){}
        Require(requests==1,"Invalid image must not be transmitted.");
    }

    static async Task TestDispatchAsync(FlowGenerationRequest generation,string expectedMethod,string root,CancellationToken ct)
    {
        var sent=0;
        using var handler=new MockHandler((request,_)=>
        {
            sent++;
            Require(request.RequestUri!.AbsolutePath.EndsWith("/"+expectedMethod,StringComparison.Ordinal),"Reference generation dispatched the wrong RPC.");
            // Immediate synthetic media success; avoids polling/sleeps and never reaches Google.
            var video=new object?[8];video[0]="media/result-"+expectedMethod;
            var generated=new object?[9];generated[8]="https://storage.googleapis.com/flow-reference-selftest/result.mp4";
            video[7]=new object?[]{generated};
            return Task.FromResult(Response(new object?[]{null,null,null,new object?[]{video}}));
        });
        using var api=new HttpClient(handler);
        using var downloads=new HttpClient(new MockHandler((request,_)=>
        {
            Require(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"),"Download must not receive account credentials.");
            var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent("synthetic media bytes"u8.ToArray())};
            response.Content.Headers.ContentType=new MediaTypeHeaderValue("video/mp4");
            return Task.FromResult(response);
        }));
        var results=await new FlowGenerationService(api,downloads).GenerateAsync(generation,Credential,root,cancellationToken:ct).ConfigureAwait(false);
        Require(sent==1 && results.Count==1 && File.Exists(results[0].LocalPath),"Reference generation should submit exactly once and persist result.");
    }

    static JsonElement Json(object value){using var document=JsonDocument.Parse(JsonSerializer.Serialize(value));return document.RootElement.Clone();}
    static HttpResponseMessage Response(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json+protobuf")};
    static void Require(bool success,string message){if(!success)throw new InvalidOperationException(message);}
    static void Reject(Action action,string message){try{action();}catch(ArgumentException){return;}throw new InvalidOperationException(message);}
    sealed class MockHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request,ct);}
}
