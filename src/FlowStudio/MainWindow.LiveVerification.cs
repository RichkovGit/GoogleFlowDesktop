using System.IO;
using FlowStudio.Services;

namespace FlowStudio;
public partial class MainWindow
{
    sealed record LiveStep(string Name,string State,string Detail,IReadOnlyList<FlowGeneratedMedia>? Files=null);
    async Task RunBrowserLiveVerificationAsync()
    {
        var retryTag=Environment.GetCommandLineArgs().Contains("--verify-region-fix2")?"-region-fix2":Environment.GetCommandLineArgs().Contains("--verify-region-fix")?"-region-fix":"";
        var runArgument=Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith("--verify-run="))?[13..];
        if(runArgument!=null&&(runArgument.Length>50||!runArgument.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-')))throw new ArgumentException("Некорректное имя проверки.");
        var directory=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"artifacts","live-verification",runArgument==null?"browser-app-20261021"+retryTag:"browser-app-"+runArgument));
        Directory.CreateDirectory(directory);var marker=Path.Combine(directory,"run.started");
        if(File.Exists(marker)){Status("Тест уже запускался. Повтор платных действий заблокирован; проверьте отчёт.");return;}
        await File.WriteAllTextAsync(marker,DateTimeOffset.UtcNow.ToString("O"));
        var steps=new List<LiveStep>();var projectId="";double? before=null,after=null;
        async Task Report()=>await ProjectStore.SaveAsync(Path.Combine(directory,"report.json"),new{application="Flow Studio",method="external-browser-official-ui",projectId,beforeCredits=before,afterCredits=after,steps,updatedUtc=DateTimeOffset.UtcNow});
        await projectOperation.WaitAsync(lifetime.Token);
        generating=true;generationCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var ct=generationCancellation.Token;
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);generationCompletion=completion;
        try
        {
            connectionMethod.SelectedIndex=1;countryCompatibility.IsChecked=true;
            await PrepareServiceBrowserAsync(ct);await serviceBrowser.StopAsync();
            before=(await new FlowAccountService(http).GetCreditsAsync(FlowCredential,ct)).FlowCredits;
            var reuse=Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith("--verify-project="))?[17..];
            var project=reuse==null?await new FlowGenerationService(http,mediaHttp).CreateProjectAsync("Flow Studio · Browser QA 22.10",FlowCredential,ct):new FlowCloudProject(FlowCloudLibraryService.NormalizeProjectId(reuse),"Flow Studio · Browser QA 22.10");projectId=project.Id;
            if(!await SaveCurrentAsync())throw new IOException("Текущий проект не сохранён; тест остановлен.");
            var newPath=ProjectStore.CreateProject(project.Name);await ProjectStore.SaveAsync(newPath,new StudioProject{Name=project.Name,CloudProjectId=projectId});
            await LoadProjectAsync(newPath);
            steps.Add(new("project","passed",projectId));await Report();
            foreach(var resolution in Environment.GetCommandLineArgs().Contains("--verify-agent-only")?Array.Empty<string>():new[]{"360p","720p"})
            {
                var model=flowModels.First(m=>m.ModelKey==(resolution=="360p"?"abra_t2v_4s_360p":"abra_t2v_4s"));
                var prompt=resolution=="360p"?"Flow Studio desktop test: a tiny orange robot gently waves beside a green seedling on a sunny windowsill, cinematic close-up, no text.":"Flow Studio desktop test: a friendly blue robot watches a paper boat float across a small pond, cinematic gentle camera movement, no text.";
                var request=new FlowGenerationRequest(projectId,model.ModelKey,prompt,"video","16:9",1,OutputResolution:resolution=="360p"?4:1);
                steps.Add(new(resolution+"-x1","submitted-once","No automatic retry."));await Report();
                var files=await GenerateThroughBrowserAsync(request,Path.Combine(directory,resolution),new Progress<string>(s=>Status(resolution+": "+s)),ct,saveProof:true);
                foreach(var file in files){var info=await exporter.InspectAsync(file.LocalPath,ct);if(info.Height!=(resolution=="360p"?360:720)||Math.Abs(info.DurationSeconds-4)>0.75)throw new InvalidOperationException("Фактическое разрешение или длительность не совпали: "+info);}
                steps.Add(new(resolution+"-x1","passed","Downloaded original; dimensions and 4-second duration verified by FFmpeg.",files));await Report();
                foreach(var file in files)assets.Add(new(){Path=file.LocalPath,Name=resolution+" · тест из приложения",Kind=file.Kind,IsGenerated=true,CloudMediaName=file.MediaName,CloudProjectId=projectId,CloudWorkflowId=file.WorkflowId??"",Prompt=prompt,Model=model.ModelKey});
                SelectAsset(assets.Last());UpdateCounts();MarkDirty();await SaveCurrentAsync();
            }
            using var agent=new FlowAgentService(http,flowAuth);
            // Dedicated test project only. The user's projects/policies are untouched.
            await agent.UpdateDefaultsAsync(projectId,"video",new("abra_t2v_10s_360p",2,1),FlowCredential,ct);
            await agent.UpdateApprovalPolicyAsync(projectId,2,FlowCredential,ct);
            var library=new FlowCloudLibraryService(http,mediaHttp);var known=(await library.ListMediaAsync(projectId,FlowCredential,ct)).Select(m=>m.MediaName).ToHashSet();
            const string story="Создай тестовый мини-сюжет из РОВНО трёх видео, каждый отдельный кадр по 10 секунд, строго 360p, x1, Omni 1.1 Flash, 16:9. Не создавай изображения, персонажей-ассетов или дополнительные варианты. Все три видео про маленького доброго оранжевого робота и зелёный росток. Кадр 01: робот находит увядший росток на солнечном подоконнике. Кадр 02: робот осторожно поливает росток, листья поднимаются. Кадр 03: робот и выросшее растение встречают закат. Мягкий кинематографичный свет, плавная камера, без текста. Названия начинай с 01, 02, 03. Используй только указанную модель и параметры; если они недоступны, сообщи об этом без замены модели и без генераций.";
            steps.Add(new("agent-story","submitted-once","3 videos requested, 360p, 10 seconds each, x1. No automatic retry."));await Report();
            var answer=await SendAgentThroughBrowserAsync(projectId,story,null,new Progress<string>(s=>{agentHistory.Text=s;Status("Агент создаёт тестовый сюжет…");}),ct,Path.Combine(directory,"agent"));
            steps.Add(new("agent-response","passed",answer.Turns.LastOrDefault()?.AssistantText??""));await Report();
            if(answer.Turns.LastOrDefault()?.RequiresApproval==true)throw new InvalidOperationException("Агент запросил отдельное подтверждение. Генерация автоматически не разрешалась.");
            var end=DateTime.UtcNow.AddMinutes(12);var storyFiles=new List<FlowGeneratedMedia>();
            while(DateTime.UtcNow<end)
            {
                var media=(await library.ListMediaAsync(projectId,FlowCredential,ct)).Where(m=>!known.Contains(m.MediaName)&&m.Kind=="video").OrderBy(m=>m.Title).ToArray();
                if(media.Any(m=>m.Status is "failed" or "canceled"))throw new InvalidOperationException("Один из кадров сюжета завершился ошибкой Google.");
                if(media.Length==3&&media.All(m=>m.Status is not("pending" or "scheduled")))
                {
                    foreach(var item in media)
                    {
                        var path=await library.DownloadMediaAsync(item.MediaName,projectId,"video",FlowCredential,Path.Combine(directory,"story"),ct);
                        var info=await exporter.InspectAsync(path,ct);if(info.Height!=360||Math.Abs(info.DurationSeconds-10)>0.75)throw new InvalidOperationException("Агент создал кадр с другими параметрами: "+info);
                        storyFiles.Add(new(item.MediaName,path,"video",item.WorkflowId));
                    }
                    break;
                }
                await Task.Delay(4000,ct);
            }
            if(storyFiles.Count!=3)throw new TimeoutException("Три готовых кадра не появились в медиатеке; повтор не выполнялся.");
            steps.Add(new("agent-story","passed","Three actual 360p/10-second originals verified.",storyFiles));await Report();
            var stitched=await exporter.ExportTimelineAsync(storyFiles.Select(f=>new MediaTimelineClip(f.LocalPath)).ToArray(),Path.Combine(directory,"story-30s.mp4"),new(640,360,30,MediaEncoderPreference.Auto),null,ct);
            steps.Add(new("story-stitch","passed",stitched.OutputPath+"; "+stitched.EncoderName));
            await agent.UpdateApprovalPolicyAsync(projectId,1,FlowCredential,ct);
        }
        catch(Exception ex){steps.Add(new("verification","failed",ex.Message));Status("Тест остановлен: "+ex.Message);}
        finally
        {
            try{after=(await new FlowAccountService(http).GetCreditsAsync(FlowCredential,lifetime.Token)).FlowCredits;}catch{}
            await serviceBrowser.StopAsync();await Report();generating=false;generationCancellation.Dispose();generationCancellation=null;projectOperation.Release();completion.TrySetResult(true);
        }
    }
}
