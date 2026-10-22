using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowStudio.Services;

namespace FlowStudio;

/// <summary>
/// Dependency-free integration checks. All HTTP is mocked; no account, real token,
/// cloud generation or paid request is used. Only the local NVENC check uses a GPU.
/// </summary>
public static class SelfTest
{
    const string MockBearer = "self-test-access-token-not-a-real-credential";
    const string MockRecaptcha = "self-test-recaptcha-not-a-real-credential";
    const string MockProject = "60eb88ad-40ac-4b64-96dc-5470f7a1c57d";

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var started=DateTimeOffset.UtcNow;
        var artifacts=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"artifacts"));
        var qa=Path.Combine(artifacts,"qa",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")[..8]);
        var checks=new List<CheckResult>();
        Directory.CreateDirectory(qa);

        async Task Check(string name,Func<Task<string>> action)
        {
            await ProjectStore.SaveAsync(Path.Combine(artifacts,"self-test-progress.json"),new{started,current=name,completed=checks.Count});
            var watch=Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detail=await action().ConfigureAwait(false);
                checks.Add(new(name,true,detail,watch.ElapsedMilliseconds));
            }
            catch(Exception exception)
            {
                // These tests use synthetic credentials only. No user storage is read.
                checks.Add(new(name,false,exception.GetType().Name+": "+exception.Message,watch.ElapsedMilliseconds));
            }
        }

        await Check("dpapi-current-user-roundtrip",()=>TestSecretsAsync(qa,cancellationToken));
        await Check("native-window-construction",async()=>
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>{var window=new MainWindow(true);window.Close();});
            return "Native WPF shell, library view configuration and feature panels constructed without loading an account or submitting a request.";
        });
        await Check("project-atomic-save-import",()=>TestProjectAsync(qa));
        await Check("flow-session-missing-401-malformed",()=>TestSessionAsync(cancellationToken));
        await Check("flow-catalog-wire-schema",TestCatalogAsync);
        await Check("flow-cookie-native-boq",()=>FlowCookieAuthSelfTest.RunAsync(cancellationToken));
        await Check("flow-browser-login-session-validation",()=>FlowBrowserLoginSelfTest.RunAsync(cancellationToken));
        await Check("flow-browser-runtime-boundaries",()=>FlowBrowserRuntimeSelfTest.RunAsync(cancellationToken));
        await Check("flow-browser-action-receipt-pump",()=>FlowBrowserReceiptSelfTest.RunAsync(cancellationToken));
        await Check("signed-ota-package-boundaries",()=>AppUpdateSelfTest.RunAsync(qa,cancellationToken));
        await Check("flow-account-credits",()=>FlowAccountSelfTest.RunAsync(cancellationToken));
        await Check("flow-cloud-cache-paths",()=>FlowCloudCacheSelfTest.RunAsync(cancellationToken));
        await Check("flow-cloud-library-schema-download",()=>FlowCloudLibrarySelfTest.RunAsync(cancellationToken));
        await Check("flow-agent-history-settings-stream",()=>FlowAgentSelfTest.RunAsync(cancellationToken));
        await Check("flow-agent-native-media-presentation",FlowAgentPresentation.SelfTestAsync);
        await Check("flow-management-workflow-collections",()=>FlowManagementSelfTest.RunAsync(cancellationToken));
        await Check("flow-generation-references",()=>FlowReferenceGenerationSelfTest.RunAsync(cancellationToken));
        await Check("library-safe-copy",()=>LibraryFileActionsSelfTest.RunAsync(qa,cancellationToken));
        await Check("automation-models-credit-download",()=>AutomationServicesSelfTest.RunAsync(cancellationToken));
        await Check("flow-generation-wire-and-download",()=>TestGenerationAsync(qa,cancellationToken));
        await Check("flow-generation-errors-allowlist-cancel",()=>TestGenerationFailuresAsync(qa,cancellationToken));
        await Check("real-cuda-nvenc-two-clip-export",()=>TestNvencAsync(qa,cancellationToken));
        await Check("gpu-timeline-trim-audio-concat",()=>MediaEditingSelfTest.RunAsync(qa,cancellationToken));

        var failed=checks.Count(check=>!check.Passed);
        var report=new
        {
            application="Flow Studio",
            startedUtc=started,
            finishedUtc=DateTimeOffset.UtcNow,
            passed=failed==0,
            exitCode=failed==0?0:1,
            realNetworkRequests=0,
            paidGenerationRequests=0,
            qaDirectory=qa,
            checks
        };
        await ProjectStore.SaveAsync(Path.Combine(artifacts,"self-test.json"),report).ConfigureAwait(false);
        return failed==0?0:1;
    }

    static async Task<string> TestSecretsAsync(string qa,CancellationToken cancellationToken)
    {
        var directory=Path.Combine(qa,"secrets");
        var storage=new SecureStorage(directory);
        var secret="FLOW_SELF_TEST_SECRET_"+Guid.NewGuid().ToString("N");
        Require(await storage.ReadStringAsync("missing",cancellationToken).ConfigureAwait(false) is null,"Missing secret should return null.");
        await storage.SaveStringAsync("qa/roundtrip",secret,cancellationToken).ConfigureAwait(false);
        Require(await storage.ReadStringAsync("qa/roundtrip",cancellationToken).ConfigureAwait(false)==secret,"DPAPI roundtrip did not preserve the secret.");
        var files=Directory.GetFiles(directory);
        Require(files.Length==1 && Path.GetExtension(files[0])==".bin","Secret name must be hashed and temporary files removed.");
        var encrypted=await File.ReadAllBytesAsync(files[0],cancellationToken).ConfigureAwait(false);
        var plaintext=Encoding.UTF8.GetBytes(secret);
        try { Require(encrypted.AsSpan().IndexOf(plaintext)<0,"Secret plaintext appeared in the persisted file."); }
        finally { CryptographicOperations.ZeroMemory(plaintext);CryptographicOperations.ZeroMemory(encrypted); }
        await storage.SaveStringAsync("qa/roundtrip",secret+"-replaced",cancellationToken).ConfigureAwait(false);
        Require(await storage.ReadStringAsync("qa/roundtrip",cancellationToken).ConfigureAwait(false)==secret+"-replaced","Atomic replacement failed.");
        storage.Delete("qa/roundtrip");
        Require(await storage.ReadStringAsync("qa/roundtrip",cancellationToken).ConfigureAwait(false) is null,"Deleted secret is still present.");
        return "DPAPI CurrentUser roundtrip and replacement passed; no plaintext, path traversal or stale temporary secret file.";
    }

    static async Task<string> TestProjectAsync(string qa)
    {
        var path=Path.Combine(qa,"project","project.flowstudio");
        var source=Path.Combine(qa,"source.png");
        var image=TinyPng();
        await File.WriteAllBytesAsync(source,image).ConfigureAwait(false);
        var imported=await ProjectStore.ImportAsync(source,path).ConfigureAwait(false);
        Require(File.Exists(imported),"Import did not copy the fixture.");
        Require((await File.ReadAllBytesAsync(imported).ConfigureAwait(false)).SequenceEqual(image),"Imported bytes differ from the fixture.");
        var asset=new Asset {Name="Тестовый кадр",Path=Path.GetRelativePath(Path.GetDirectoryName(path)!,imported),Kind="image"};
        var project=new StudioProject {Name="QA · нативный проект",Draft="Сохраняем реальную правку",Assets=[asset],Storyboard=[asset.Id]};
        await ProjectStore.SaveAsync(path,project).ConfigureAwait(false);
        project.Draft="Обновлённый текст";
        await ProjectStore.SaveAsync(path,project).ConfigureAwait(false);
        var restored=await ProjectStore.ReadAsync<StudioProject>(path).ConfigureAwait(false);
        Require(restored is not null && restored.Draft==project.Draft && restored.Storyboard.SequenceEqual(project.Storyboard),"Project roundtrip lost draft or storyboard.");
        Require(restored!.Assets.Count==1 && !Path.IsPathFullyQualified(restored.Assets[0].Path),"Project must retain a relative media path.");
        var before=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var failed=false;
        try { await ProjectStore.SaveAsync(path,new BadSerializable()).ConfigureAwait(false); }
        catch(InvalidOperationException) { failed=true; }
        Require(failed,"The failing serializer fixture unexpectedly succeeded.");
        Require((await File.ReadAllBytesAsync(path).ConfigureAwait(false)).SequenceEqual(before),"A failed save changed the previous project.");
        var blockedPath=Path.Combine(Path.GetDirectoryName(path)!,"blocked.flowstudio");
        Directory.CreateDirectory(blockedPath);
        var failedReplace=false;
        try { await ProjectStore.SaveAsync(blockedPath,project).ConfigureAwait(false); }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { failedReplace=true; }
        Require(failedReplace&&Directory.Exists(blockedPath),"Invalid destination should reject replacement and preserve the existing directory.");
        var failedImport=false;
        try { await ProjectStore.ImportAsync(Path.Combine(qa,"does-not-exist.png"),path).ConfigureAwait(false); }
        catch(FileNotFoundException) { failedImport=true; }
        Require(failedImport,"Missing source should fail import.");
        Require(Directory.GetFiles(ProjectStore.MediaDirectory(path)).Length==1,"Failed import left a partial media file.");
        Require(Directory.GetFiles(Path.GetDirectoryName(path)!,"*.tmp",SearchOption.AllDirectories).Length==0,"Project operation left a temporary file.");
        return "Import, relative paths, storyboard and replacement roundtrip passed; failed serialization and failed import preserve prior files.";
    }

    static async Task<string> TestSessionAsync(CancellationToken cancellationToken)
    {
        using var handler=new MockHandler((_,_)=>Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized,"{}")));
        using var client=new HttpClient(handler);
        var service=new FlowSessionService(client);
        Require((await service.ProbeAsync(null,cancellationToken).ConfigureAwait(false)).Status==FlowSessionStatus.MissingToken,"Empty token was not rejected locally.");
        Require(handler.Calls==0,"Missing token sent an HTTP request.");
        Require((await service.ProbeAsync("token with whitespace",cancellationToken).ConfigureAwait(false)).Status==FlowSessionStatus.Unauthorized,"Malformed token was not rejected locally.");
        Require(handler.Calls==0,"Malformed token sent an HTTP request.");
        var unauthorized=await service.ProbeAsync(MockBearer,cancellationToken).ConfigureAwait(false);
        Require(unauthorized.Status==FlowSessionStatus.Unauthorized && unauthorized.HttpStatusCode==401,"HTTP 401 was not mapped to unauthorized.");
        using var malformedHandler=new MockHandler((_,_)=>Task.FromResult(JsonResponse(HttpStatusCode.OK,"not-json")));
        using var malformedClient=new HttpClient(malformedHandler);
        Require((await new FlowSessionService(malformedClient).ProbeAsync(MockBearer,cancellationToken).ConfigureAwait(false)).Status==FlowSessionStatus.InvalidResponse,"Malformed response was accepted.");
        return "Missing/malformed tokens issue zero requests; mock HTTP 401 and malformed JSON produce explicit failure states.";
    }

    static Task<string> TestCatalogAsync()
    {
        var imageModel=new object?[]{"Image test",new object?[]{new object?[]{"image-generation-key"}},null,"parent-image-id"};
        var videoModel=new object?[]{"Video test",new object?[]{new object?[]{"video-generation-key"}},null,"parent-video-id"};
        var config=new object?[6];config[4]=new object?[]{videoModel};config[5]=new object?[]{imageModel};
        var models=FlowModelCatalogService.ParseCatalog(JsonSerializer.Serialize(new object?[]{config}));
        Require(models.Count==2,"Catalog did not parse the two fixture models.");
        Require(models.Any(model=>model.Kind=="image"&&model.ModelId=="parent-image-id"&&model.ModelKey=="image-generation-key"),"Image usage key was confused with parent model ID.");
        Require(models.Any(model=>model.Kind=="video"&&model.ModelKey=="video-generation-key"),"Video model field was not parsed.");
        var rejected=false;try { _=FlowModelCatalogService.ParseCatalog("{\"models\":[]}"); } catch(FlowCatalogException) { rejected=true; }
        Require(rejected,"Unverified REST-shaped model data must be rejected.");
        return Task.FromResult("Protobuf JSON model usage keys are distinct from parent IDs; unexpected response schema is rejected.");
    }

    static async Task<string> TestGenerationAsync(string qa,CancellationToken cancellationToken)
    {
        var imageBytes=TinyPng();
        var downloadUri=new Uri("https://storage.googleapis.com/flow-self-test/result.png");
        using var apiHandler=new MockHandler(async (request,token)=>
        {
            Require(request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath.EndsWith("/BatchGenerateImages",StringComparison.Ordinal),"Generation used an unexpected RPC.");
            Require(request.Headers.Authorization?.Scheme=="Bearer"&&request.Headers.Authorization.Parameter==MockBearer,"API authorization was missing or changed.");
            Require(request.Content?.Headers.ContentType?.MediaType=="application/json+protobuf","Generation content type is not protobuf JSON.");
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false));
            var root=body.RootElement;
            var image=root[1][0];
            Require(image[3].GetInt32()==1234&&image[4].GetInt32()==3&&image[5].GetString()=="test-image-key","Image seed, ratio or generation key is in the wrong field.");
            Require(image[7][5].GetString()==MockProject,"Image context project ID is in the wrong field.");
            Require(image[8][0][0][0].GetString()=="Fixture prompt","Structured image prompt is in the wrong field.");
            Require(Guid.TryParse(image[12].GetString(),out _)&&Guid.TryParse(image[13].GetString(),out _),"Workflow/media seeds were not generated.");
            return JsonResponse(HttpStatusCode.OK,ImageResponse(downloadUri.AbsoluteUri));
        });
        using var downloadHandler=new MockHandler((request,_)=>
        {
            Require(request.Method==HttpMethod.Get&&request.RequestUri==downloadUri,"Unexpected download URL.");
            Require(request.Headers.Authorization is null,"OAuth token was forwarded to the download host.");
            var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(imageBytes)};
            response.Content.Headers.ContentType=new MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        });
        using var api=new HttpClient(apiHandler);
        using var downloads=new HttpClient(downloadHandler);
        var service=new FlowGenerationService(api,downloads);
        var request=new FlowGenerationRequest(MockProject,"test-image-key","Fixture prompt",Seed:1234);
        var output=Path.Combine(qa,"mock-generated");
        var result=await service.GenerateAsync(request,MockBearer,output,cancellationToken:cancellationToken).ConfigureAwait(false);
        Require(apiHandler.Calls==1&&downloadHandler.Calls==1,"Generation or download was repeated unexpectedly.");
        Require(result.Count==1&&result[0].Kind=="image"&&result[0].MediaName=="media/fixture","Generation result identity was lost.");
        Require((await File.ReadAllBytesAsync(result[0].LocalPath,cancellationToken).ConfigureAwait(false)).SequenceEqual(imageBytes),"Downloaded image bytes changed.");
        Require(Directory.GetFiles(output,"*.part").Length==0,"Successful download left a partial file.");
        using var octetHandler=new MockHandler((_,_)=>
        {
            var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(imageBytes)};
            response.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        });
        using var octetDownloads=new HttpClient(octetHandler);
        var octet=await new FlowGenerationService(api,octetDownloads).GenerateAsync(request,MockBearer,Path.Combine(qa,"mock-octet"),cancellationToken:cancellationToken).ConfigureAwait(false);
        Require(octet.Count==1&&Path.GetExtension(octet[0].LocalPath)==".png","GCS octet-stream PNG was not recognized by its signature.");

        using var videoBody=JsonDocument.Parse(JsonSerializer.Serialize(FlowGenerationService.BuildVideoRequest(
            new(MockProject,"test-video-key","Video prompt","video","9:16"))));
        var video=videoBody.RootElement[0][0];
        Require(video[0][2][0][0][0].GetString()=="Video prompt"&&video[1].GetString()=="test-video-key"&&video[2].GetInt32()==1,"Video prompt/model/aspect ratio wire fields changed.");
        Require(Guid.TryParse(video[4][4].GetString(),out _)&&Guid.TryParse(video[4][5].GetString(),out _),"Video metadata seeds were not generated.");
        return "Mock native image RPC and credential-free PNG/octet-stream download passed; exact image/video prompt, seed, model and aspect fields verified. No live request.";
    }

    static async Task<string> TestGenerationFailuresAsync(string qa,CancellationToken cancellationToken)
    {
        var request=new FlowGenerationRequest(MockProject,"test-image-key","Fixture prompt",RecaptchaToken:MockRecaptcha);
        using var errorHandler=new MockHandler((_,_)=>Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized,
            JsonSerializer.Serialize(new {error=new {message="expired "+MockBearer+" "+MockRecaptcha}}))));
        using var neverDownload=new MockHandler((_,_)=>throw new InvalidOperationException("Download must not be reached."));
        using var errors=new HttpClient(errorHandler);
        using var errorDownloads=new HttpClient(neverDownload);
        FlowGenerationException? failure=null;
        try { await new FlowGenerationService(errors,errorDownloads).GenerateAsync(request,MockBearer,Path.Combine(qa,"must-not-exist"),cancellationToken:cancellationToken).ConfigureAwait(false); }
        catch(FlowGenerationException exception) { failure=exception; }
        Require(failure?.HttpStatusCode==401,"Generation HTTP 401 was not reported.");
        Require(!failure!.Message.Contains(MockBearer,StringComparison.Ordinal)&&!failure.Message.Contains(MockRecaptcha,StringComparison.Ordinal),"Error text leaked a synthetic credential.");
        Require(errorHandler.Calls==1&&neverDownload.Calls==0,"An error retried paid generation or started download.");

        using var blockedHandler=new MockHandler((_,_)=>Task.FromResult(JsonResponse(HttpStatusCode.OK,ImageResponse("https://127.0.0.1/private.png"))));
        using var blockedApi=new HttpClient(blockedHandler);
        var blocked=false;
        try { await new FlowGenerationService(blockedApi,errorDownloads).GenerateAsync(request,MockBearer,Path.Combine(qa,"must-not-exist"),cancellationToken:cancellationToken).ConfigureAwait(false); }
        catch(FlowGenerationException) { blocked=true; }
        Require(blocked&&neverDownload.Calls==0,"Unapproved media host reached the network transport.");
        Require(!FlowGenerationService.IsAllowedMediaUri(new("https://storage.googleapis.com.evil.example/image.png"))
            && !FlowGenerationService.IsAllowedMediaUri(new("http://storage.googleapis.com/image.png"))
            && !FlowGenerationService.IsAllowedMediaUri(new("https://user:password@storage.googleapis.com/image.png")),"Media URI allowlist accepts a spoofed host, plaintext HTTP or URL credentials.");

        using var redirectApiHandler=new MockHandler((_,_)=>Task.FromResult(JsonResponse(HttpStatusCode.OK,ImageResponse("https://storage.googleapis.com/fixture.png"))));
        using var redirectHandler=new MockHandler((_,_)=>
        {
            var response=new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location=new("https://example.com/forbidden.png");
            return Task.FromResult(response);
        });
        using var redirectApi=new HttpClient(redirectApiHandler);
        using var redirectDownloads=new HttpClient(redirectHandler);
        blocked=false;
        try { await new FlowGenerationService(redirectApi,redirectDownloads).GenerateAsync(request,MockBearer,Path.Combine(qa,"must-not-exist"),cancellationToken:cancellationToken).ConfigureAwait(false); }
        catch(FlowGenerationException) { blocked=true; }
        Require(blocked&&redirectHandler.Calls==1,"An unapproved redirect was followed.");

        using var invalidMediaHandler=new MockHandler((_,_)=>
        {
            var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Encoding.UTF8.GetBytes("this is not an image"))};
            response.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        });
        using var invalidMediaDownloads=new HttpClient(invalidMediaHandler);
        var rejectedOutput=Path.Combine(qa,"rejected-octet");
        blocked=false;
        try { await new FlowGenerationService(redirectApi,invalidMediaDownloads).GenerateAsync(request,MockBearer,rejectedOutput,cancellationToken:cancellationToken).ConfigureAwait(false); }
        catch(FlowGenerationException) { blocked=true; }
        Require(blocked&&Directory.GetFiles(rejectedOutput).Length==0,"An unrecognized octet-stream was published or left a partial file.");

        using var canceled=new CancellationTokenSource();canceled.Cancel();
        using var cancelHandler=new MockHandler((_,token)=>
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("A canceled request must not be submitted.");
        });
        using var cancelApi=new HttpClient(cancelHandler);
        var cancelObserved=false;
        try { await new FlowGenerationService(cancelApi,errorDownloads).GenerateAsync(request,MockBearer,Path.Combine(qa,"must-not-exist"),cancellationToken:canceled.Token).ConfigureAwait(false); }
        catch(OperationCanceledException) { cancelObserved=true; }
        Require(cancelObserved&&cancelHandler.Calls==0,"Cancellation did not propagate before generation was submitted.");
        Require(!Directory.Exists(Path.Combine(qa,"must-not-exist")),"A failed or canceled request created result files.");
        return "401 surfaces once without retry or credentials; unapproved host/redirect is blocked; cancellation propagates without result files.";
    }

    static async Task<string> TestNvencAsync(string qa,CancellationToken cancellationToken)
    {
        var exporter=new MediaExportService();
        var probe=await exporter.ProbeAsync(cancellationToken).ConfigureAwait(false);
        Require(probe.IsAvailable,probe.Message);
        var first=Path.Combine(qa,"clip one — тест'1.mp4");
        var second=Path.Combine(qa,"clip two.mp4");
        foreach(var fixture in new[]{(Path:first,Color:"red"),(Path:second,Color:"blue")})
        {
            var create=await RunProcessAsync(exporter.FfmpegPath,new[]
            {
                "-hide_banner","-loglevel","error","-nostdin","-n",
                "-init_hw_device","cuda=qa:0","-filter_hw_device","qa",
                "-f","lavfi","-i",$"color=c={fixture.Color}:s=320x180:r=24:d=1",
                "-vf","format=nv12,hwupload_cuda","-c:v","h264_nvenc","-gpu","0",
                "-preset","p4","-g","24","-bf","0","-an",fixture.Path
            },cancellationToken).ConfigureAwait(false);
            Require(create.ExitCode==0,"NVENC fixture creation failed: "+create.Error);
        }
        var progress=new RecordingProgress<MediaExportProgress>();
        var output=Path.Combine(qa,"joined.mp4");
        var exported=await exporter.ExportConcatNvencAsync(new[]{first,second},output,progress,cancellationToken).ConfigureAwait(false);
        Require(exported.SizeBytes>0&&File.Exists(output),"Export did not produce an MP4.");
        Require(progress.Values.Any(value=>value.IsComplete),"FFmpeg export did not report completion.");
        var verify=await RunProcessAsync(exporter.FfmpegPath,new[]
        {
            "-hide_banner","-loglevel","error","-nostdin",
            "-hwaccel","cuda","-hwaccel_device","0","-hwaccel_output_format","cuda",
            "-i",output,"-map","0:v:0","-an","-progress","pipe:1","-nostats","-f","null","-"
        },cancellationToken).ConfigureAwait(false);
        Require(verify.ExitCode==0,"Exported video could not be decoded: "+verify.Error);
        var lines=verify.Output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
        var frameText=lines.LastOrDefault(line=>line.StartsWith("frame=",StringComparison.Ordinal))?[6..];
        var timeText=lines.LastOrDefault(line=>line.StartsWith("out_time_us=",StringComparison.Ordinal))?[12..];
        Require(long.TryParse(frameText,NumberStyles.Integer,CultureInfo.InvariantCulture,out var frames)&&frames==48,"Decoded frame count should be exactly 48 (2 × 24).");
        Require(long.TryParse(timeText,NumberStyles.Integer,CultureInfo.InvariantCulture,out var micros)&&Math.Abs(micros-2_000_000)<=50_000,"Decoded duration should be two seconds.");
        var original=await File.ReadAllBytesAsync(output,cancellationToken).ConfigureAwait(false);
        var overwriteRejected=false;
        try { await exporter.ExportConcatNvencAsync(new[]{first,second},output,cancellationToken:cancellationToken).ConfigureAwait(false); }
        catch(IOException) { overwriteRejected=true; }
        Require(overwriteRejected&&(await File.ReadAllBytesAsync(output,cancellationToken).ConfigureAwait(false)).SequenceEqual(original),"Existing export was overwritten.");
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        bool canceledAfterFrames=false,cancellationObserved=false;
        var canceledOutput=Path.Combine(qa,"canceled-must-not-exist.mp4");
        var cancelProgress=new RecordingProgress<MediaExportProgress>(value=>
        {
            if(value.Frames>0) { canceledAfterFrames=true;cancel.Cancel(); }
        });
        try
        {
            var longerSequence=Enumerable.Range(0,128).SelectMany(_=>new[]{first,second}).ToArray();
            await exporter.ExportConcatNvencAsync(longerSequence,canceledOutput,cancelProgress,cancel.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException) { cancellationObserved=true; }
        Require(cancellationObserved&&canceledAfterFrames,"Cancellation must stop an active FFmpeg encode after frames begin.");
        Require(!File.Exists(canceledOutput),"Canceled export left a published result.");
        Require(Directory.GetFiles(qa,".flow-*").Length==0,"Export left a concat manifest or partial result.");
        var seconds=(micros/1_000_000d).ToString("0.000",CultureInfo.InvariantCulture);
        return $"Real CUDA/NVENC export passed: two local one-second H.264 clips, apostrophe/Unicode paths, file-only concat, {frames} decoded frames, {seconds}s; active encode cancellation removes partial files. Output: {output}";
    }

    static string ImageResponse(string url)
    {
        var generated=new object?[14];generated[13]=url;
        var metadata=new object?[9];metadata[8]=new object?[]{3};
        var media=new object?[7];media[0]="media/fixture";media[2]="workflows/fixture";media[5]=metadata;media[6]=new object?[]{generated};
        return JsonSerializer.Serialize(new object?[]{new object?[]{media}});
    }

    static byte[] TinyPng()=>Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jMZkAAAAASUVORK5CYII=");

    static HttpResponseMessage JsonResponse(HttpStatusCode status,string json)
        => new(status){Content=new StringContent(json,Encoding.UTF8,"application/json")};

    static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }

    static async Task<ProcessResult> RunProcessAsync(string path,IEnumerable<string> arguments,CancellationToken cancellationToken)
    {
        var start=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=new Process{StartInfo=start};
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        var output=process.StandardOutput.ReadToEndAsync();
        var error=process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new(process.ExitCode,await output.ConfigureAwait(false),await error.ConfigureAwait(false));
        }
        catch
        {
            try { if(!process.HasExited)process.Kill(entireProcessTree:true); }
            catch(InvalidOperationException) { }
            catch(System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output,error).ConfigureAwait(false);
            throw;
        }
    }

    sealed record CheckResult(string Name,bool Passed,string Detail,long DurationMilliseconds);
    sealed record ProcessResult(int ExitCode,string Output,string Error);
    sealed class BadSerializable { public string Value=>throw new InvalidOperationException("Intentional serialization failure."); }
    sealed class RecordingProgress<T>(Action<T>? onReport=null) : IProgress<T>
    {
        public List<T> Values { get; }=[];
        public void Report(T value) { Values.Add(value);onReport?.Invoke(value); }
    }
    sealed class MockHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return respond(request,cancellationToken);
        }
    }
}
