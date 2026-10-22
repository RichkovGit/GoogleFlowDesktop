using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

internal static class FlowCloudLibrarySelfTest
{
    public static async Task<string> RunAsync(CancellationToken ct=default)
    {
        const string project="fc73dcf8-095c-44a3-b222-d4b13719118b";
        const string media="media/synthetic-video";
        var count=0;
        var video=Message((1,media),(2,project),(3,"workflow-1"),(6,Message((1,new object?[]{"1760000000",100}),(9,new object?[]{3}),(11,"https://storage.googleapis.com/synthetic-preview"))),(8,Message((1,Message((8,"A synthetic scene"),(9,"https://flow-content.google/synthetic.mp4"),(13,"synthetic-model"))))));
        var workflow=Message((1,"workflow-1"),(4,Message((1,"A saved title"),(2,new object?[]{"1760000000",100}))));
        using var api=new HttpClient(new Fixture(async request=>
        {
            var body=await request.Content!.ReadAsStringAsync(ct);using var json=JsonDocument.Parse(body);
            if(request.RequestUri!.AbsolutePath.EndsWith("/GetProjects",StringComparison.Ordinal))
            {
                count++;
                Require(json.RootElement[0].GetString()=="projects/*"&&json.RootElement[1].GetInt32()==21,"Project request wire mismatch");
                if(count==1)return Json(new object?[]{new object?[]{new object?[]{project,new object?[]{"Fixture",null,new object?[]{"1760000000",0}}}},"next"});
                Require(json.RootElement[2].GetString()=="next","Pagination token lost");return Json(new object?[]{Array.Empty<object>()});
            }
            if(request.RequestUri.AbsolutePath.EndsWith("/GetProjectContents",StringComparison.Ordinal))
            {
                Require(json.RootElement[0].GetString()=="projects/"+project,"Wrong cloud project contents");
                return Json(Message((2,new object?[]{workflow}),(3,new object?[]{video,video})));
            }
            if(request.RequestUri.AbsolutePath.EndsWith("/GetMedia",StringComparison.Ordinal))
            {
                Require(json.RootElement[0].GetString()==media,"Wrong media refresh");return Json(video);
            }
            throw new InvalidOperationException("Unexpected RPC");
        }));
        using var downloads=new HttpClient(new Fixture(request=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new ByteArrayContent(new byte[]{0,0,0,20,102,116,121,112,105,115,111,109}){Headers={ContentType=new("video/mp4")}}})));
        var service=new FlowCloudLibraryService(api,downloads);
        var projects=await service.ListProjectsAsync("synthetic-credential",ct);
        Require(projects.Count==1&&count==2&&projects[0].Title=="Fixture","Account pagination failed");
        var library=await service.ListMediaAsync(project,"synthetic-credential",ct);
        Require(library.Count==1&&library[0].Kind=="video"&&library[0].Prompt=="A synthetic scene"&&library[0].Title=="A saved title"&&library[0].Status=="success"&&library[0].ThumbnailUrl is not null,"Media metadata/dedup failed");
        var directory=Path.Combine(Path.GetTempPath(),"FlowStudio-library-qa-"+Guid.NewGuid().ToString("N"));
        try
        {
            var path=await service.DownloadMediaAsync(media,project,"video","synthetic-credential",directory,ct);
            Require(Path.GetExtension(path)==".mp4"&&new FileInfo(path).Length==12,"Native original download failed");
            Require(!Directory.GetFiles(directory).Any(p=>p.EndsWith(".part",StringComparison.Ordinal)),"Partial download remained");
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
        Require(FlowGenerationService.IsAllowedMediaUri(new Uri("https://flow-content.google/media")),"Live observed Flow content host rejected");
        Require(!FlowGenerationService.IsAllowedMediaUri(new Uri("https://flow-content.google.evil.example/media")),"Content allowlist suffix bypass");
        try{await service.ListMediaAsync("../../outside","synthetic-credential",ct);throw new InvalidOperationException("Traversal accepted");}catch(ArgumentException){}
        using var loopApi=new HttpClient(new Fixture(request=>Task.FromResult(Json(new object?[]{Array.Empty<object>(),"same"}))));
        try{await new FlowCloudLibraryService(loopApi,downloads).ListProjectsAsync("synthetic-credential",ct);throw new InvalidOperationException("Page loop accepted");}catch(FlowCloudLibraryException){}
        return "Paged account projects, native contents metadata, deduplication, refreshed original downloads, exact Google content allowlist, no partials, invalid UUID and page-loop rejection passed.";
    }
    internal static object?[] Message(params(int Field,object? Value)[] fields){var values=new object?[fields.Max(x=>x.Field)];foreach(var(f,v)in fields)values[f-1]=v;return values;}
    internal static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json+protobuf")};
    internal static void Require(bool value,string detail){if(!value)throw new InvalidOperationException(detail);}
    internal sealed class Fixture(Func<HttpRequestMessage,Task<HttpResponseMessage>> action):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>action(request);}
}
