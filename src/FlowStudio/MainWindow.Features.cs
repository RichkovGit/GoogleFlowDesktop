using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FlowStudio.Services;
using Microsoft.Win32;

namespace FlowStudio;
public partial class MainWindow
{
    UserPreferences preferences=new();
    readonly CreditMonitorService creditMonitor=new();
    readonly MediaAutoDownloadService autoDownloads=new();
    readonly DispatcherTimer creditTimer=new();
    DesktopNotificationService? notifications;
    CheckBox autoLoginCheck=null!,autoDownloadCheck=null!,autoStitchCheck=null!,creditNotifyCheck=null!,creditActionCheck=null!,dailyOnlyCheck=null!,cpuCheck=null!;
    TextBox downloadFolderBox=null!,pollMinutesBox=null!,actionPromptBox=null!,actionModelBox=null!,actionBudgetBox=null!,actionCountBox=null!;
    TextBlock automationStatus=null!,encoderStatus=null!,editInfo=null!;
    ListBox editorList=null!;
    TextBox trimStartBox=null!,trimEndBox=null!,volumeBox=null!;
    CheckBox muteCheck=null!;
    ComboBox encoderBox=null!,resolutionBox=null!;
    bool creditPolling,modelSelectionUpdating;
    DateTimeOffset lastAutoRestore=DateTimeOffset.MinValue;
    readonly SemaphoreSlim reconnectGate=new(1,1);
    sealed class RemoteMediaState {public bool Established{get;set;} public HashSet<string> Completed{get;set;}=[];}
    RemoteMediaState remoteMediaState=new();
    static string RemoteStatePath=>Path.Combine(ProjectStore.AppDirectory,"observed-media.json");

    static Asset CreateShot(Asset source,ClipEdit? edit=null)=>new()
    {
        Id=source.Id,Path=source.Path,Name=source.Name,Kind=source.Kind,Prompt=source.Prompt,Model=source.Model,Created=source.Created,IsGenerated=source.IsGenerated,Thumbnail=source.Thumbnail,
        CloudMediaName=source.CloudMediaName,CloudProjectId=source.CloudProjectId,CloudProjectTitle=source.CloudProjectTitle,CloudWorkflowId=source.CloudWorkflowId,
        TrimStartSeconds=edit?.TrimStartSeconds??0,TrimEndSeconds=edit?.TrimEndSeconds,Volume=edit?.Volume??1,Muted=edit?.Muted??false
    };
    TextBox AddField(StackPanel body,string label,string value="",bool multiline=false)
    {
        body.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,14,0,7),Foreground=(Brush)FindResource("Muted")});
        var box=new TextBox{Text=value,AcceptsReturn=multiline,TextWrapping=multiline?TextWrapping.Wrap:TextWrapping.NoWrap,MinHeight=multiline?90:36,MaxWidth=850,HorizontalAlignment=HorizontalAlignment.Stretch};body.Children.Add(box);return box;
    }
    CheckBox AddCheck(StackPanel body,string text,bool value=false)
    {var box=new CheckBox{Content=text,IsChecked=value};body.Children.Add(box);return box;}
    void BuildEditorPage()
    {
        CreateFeaturePage("editor","Монтаж","Добавьте материалы кнопкой «В сцену». Меняйте порядок, обрезку и звук; оригиналы сохраняются.",out var body);
        var row=new WrapPanel();row.Children.Add(ActionButton("＋ Из медиатеки",()=>{SwitchPage("library");return Task.CompletedTask;}));
        row.Children.Add(ActionButton("← Раньше",()=>{MoveEditorShot(-1);return Task.CompletedTask;}));row.Children.Add(ActionButton("Позже →",()=>{MoveEditorShot(1);return Task.CompletedTask;}));
        row.Children.Add(ActionButton("Убрать из монтажа",()=>{if(exporting)return Task.CompletedTask;if(editorList.SelectedIndex>=0){shots.RemoveAt(editorList.SelectedIndex);UpdateCounts();MarkDirty();}return Task.CompletedTask;}));body.Children.Add(row);
        editorList=new ListBox{ItemsSource=shots,DisplayMemberPath="Name",Height=185};editorList.SelectionChanged+=async(_,_)=>
        {
            if(editorList.SelectedItem is not Asset clip)return;
            trimStartBox.Text=clip.TrimStartSeconds.ToString("0.###",CultureInfo.InvariantCulture);trimEndBox.Text=clip.TrimEndSeconds?.ToString("0.###",CultureInfo.InvariantCulture)??"";volumeBox.Text=(clip.Volume*100).ToString("0",CultureInfo.InvariantCulture);muteCheck.IsChecked=clip.Muted;
            try{await EnsureCloudAssetAsync(clip,lifetime.Token);if(clip.Kind=="video"){var info=await exporter.InspectAsync(clip.Path,lifetime.Token);editInfo.Text=$"Исходник: {info.DurationSeconds:0.##} сек · {info.Width} × {info.Height} · "+(info.HasAudio?"со звуком":"без звука");}else editInfo.Text="Изображения доступны в раскадровке JSON. Для MP4 добавьте видеоклипы.";}
            catch(Exception ex){editInfo.Text=ex.Message;}
        };body.Children.Add(editorList);
        editInfo=new TextBlock{Text="Выберите клип, чтобы изменить обрезку и звук.",Margin=new Thickness(0,10,0,0),Foreground=(Brush)FindResource("Muted")};body.Children.Add(editInfo);
        var columns=new UniformGrid{Columns=3,Margin=new Thickness(0,0,0,10)};
        var s1=new StackPanel{Margin=new Thickness(0,0,10,0)};trimStartBox=AddField(s1,"Начало, секунды","0");columns.Children.Add(s1);
        var s2=new StackPanel{Margin=new Thickness(0,0,10,0)};trimEndBox=AddField(s2,"Конец, секунды · пусто = до конца");columns.Children.Add(s2);
        var s3=new StackPanel();volumeBox=AddField(s3,"Громкость, %","100");columns.Children.Add(s3);body.Children.Add(columns);
        muteCheck=AddCheck(body,"Без звука");body.Children.Add(ActionButton("Применить к выбранному клипу",()=>{ApplyClipEdit();return Task.CompletedTask;}));
        body.Children.Add(new Separator());
        var exportRow=new WrapPanel();
        encoderBox=new ComboBox{Width=235,Margin=new Thickness(0,0,10,8),ItemsSource=new[]{"Авто · аппаратный кодировщик","NVIDIA NVENC","AMD AMF","Intel Quick Sync","CPU · по разрешению"},SelectedIndex=0};exportRow.Children.Add(encoderBox);
        resolutionBox=new ComboBox{Width=180,ItemsSource=new[]{"1920 × 1080 · 30 fps","1080 × 1920 · 30 fps","1280 × 720 · 30 fps"},SelectedIndex=0,Margin=new Thickness(0,0,10,8)};exportRow.Children.Add(resolutionBox);body.Children.Add(exportRow);
        cpuCheck=AddCheck(body,"Разрешить программное кодирование при выборе CPU");
        var bottom=new WrapPanel();bottom.Children.Add(ActionButton("Экспортировать монтаж MP4",ExportEditedTimelineAsync,true));bottom.Children.Add(ActionButton("Проверить видеокодировщики",ProbeAllEncodersAsync));body.Children.Add(bottom);
        encoderStatus=new TextBlock{Text="NVIDIA / AMD / Intel. Доступность проверяется на этом компьютере перед экспортом.",Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,12,0,0)};body.Children.Add(encoderStatus);
    }
    void MoveEditorShot(int direction)
    {if(exporting)return;var i=editorList.SelectedIndex;var j=i+direction;if(i<0||j<0||j>=shots.Count)return;shots.Move(i,j);editorList.SelectedIndex=j;MarkDirty();}
    void ApplyClipEdit()
    {
        if(exporting)throw new InvalidOperationException("Дождитесь завершения экспорта.");
        if(editorList.SelectedItem is not Asset clip)throw new InvalidOperationException("Сначала выберите клип в монтаже.");
        var start=ParseNumber(trimStartBox.Text,"Начало");double? end=string.IsNullOrWhiteSpace(trimEndBox.Text)?null:ParseNumber(trimEndBox.Text,"Конец");var volume=ParseNumber(volumeBox.Text,"Громкость")/100;
        if(start<0||end<=start||volume<0||volume>4)throw new InvalidOperationException("Проверьте обрезку и громкость: начало ≥ 0, конец > начала, громкость 0–400%.");
        clip.TrimStartSeconds=start;clip.TrimEndSeconds=end;clip.Volume=volume;clip.Muted=muteCheck.IsChecked==true;MarkDirty();Status("Настройки клипа сохранены");
    }
    static double ParseNumber(string text,string label)=>double.TryParse(text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var number)&&double.IsFinite(number)?number:throw new InvalidOperationException(label+": введите число.");
    MediaTimelineOptions TimelineOptions()
    {
        var encoder=encoderBox.SelectedIndex switch{1=>MediaEncoderPreference.NvidiaNvenc,2=>MediaEncoderPreference.AmdAmf,3=>MediaEncoderPreference.IntelQsv,4=>MediaEncoderPreference.Cpu,_=>MediaEncoderPreference.Auto};
        var (width,height)=resolutionBox.SelectedIndex switch{1=>(1080,1920),2=>(1280,720),_=>(1920,1080)};
        return new(width,height,30,encoder,cpuCheck.IsChecked==true);
    }
    async Task ProbeAllEncodersAsync()
    {encoderStatus.Text="Проверяем реальные кадры NVIDIA / AMD / Intel…";var capabilities=await exporter.ProbeEncodersAsync(lifetime.Token,true);encoderStatus.Text=string.Join("\n",capabilities.Select(c=>c.Message));}
    async Task ExportEditedTimelineAsync()
    {
        if(exporting||generating||preparingGeneration)throw new InvalidOperationException("Дождитесь завершения активной задачи.");
        if(shots.Count==0||shots.Any(a=>a.Kind!="video"))throw new InvalidOperationException("Для MP4 добавьте в монтаж только видеоклипы.");
        var dialog=new SaveFileDialog{Filter="Видео MP4|*.mp4",FileName=SafeFileName(projectName)+"-монтаж.mp4",OverwritePrompt=false};if(dialog.ShowDialog(this)!=true)return;
        if(File.Exists(dialog.FileName))throw new InvalidOperationException("Выберите новое имя для готового видео.");
        exportCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);exporting=true;var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);exportCompletion=completion;
        try
        {
            var clips=shots.Select(a=>CreateShot(a,new(a.TrimStartSeconds,a.TrimEndSeconds,a.Volume,a.Muted))).ToArray();var options=TimelineOptions();
            foreach(var clip in clips)await EnsureCloudAssetAsync(clip,exportCancellation.Token);
            var result=await exporter.ExportTimelineAsync(clips.Select(a=>new MediaTimelineClip(a.Path,a.TrimStartSeconds,a.TrimEndSeconds,a.Volume,a.Muted)).ToArray(),dialog.FileName,options,new Progress<MediaExportProgress>(p=>{encoderStatus.Text=$"Монтаж: {p.EncodedTime:mm\\:ss} · {p.Speed}";Status(encoderStatus.Text);}),exportCancellation.Token);
            encoderStatus.Text=$"Сохранено: {result.OutputPath}\n{result.EncoderName} · {result.SizeBytes/1024d/1024d:0.#} МБ";Status("Монтаж экспортирован");
        }
        finally{exporting=false;exportCancellation.Dispose();exportCancellation=null;completion.TrySetResult(true);}
    }
    void BuildAutomationPage()
    {
        CreateFeaturePage("automation","Автоматизация","Настройки действуют, пока Flow Studio запущен, в том числе в свёрнутом окне.",out var body);
        autoLoginCheck=AddCheck(body,"Восстанавливать вход Google при запуске",true);
        autoDownloadCheck=AddCheck(body,"Автоматически сохранять новые сгенерированные видео в выбранную папку");
        downloadFolderBox=AddField(body,"Папка автоскачивания");
        body.Children.Add(ActionButton("Выбрать папку…",()=>{var dialog=new OpenFolderDialog{Title="Куда сохранять новые видео Flow"};if(dialog.ShowDialog(this)==true)downloadFolderBox.Text=dialog.FolderName;return Task.CompletedTask;}));
        autoStitchCheck=AddCheck(body,"После нового видео добавлять его в монтаж и сохранять обновлённую склейку");
        body.Children.Add(new Separator());
        creditNotifyCheck=AddCheck(body,"Уведомлять Windows о подтверждённом увеличении баланса",true);
        pollMinutesBox=AddField(body,"Проверять кредиты каждые N минут","5");
        body.Children.Add(new TextBlock{Text="Зачисления +50 и +1000 определяются по ответу Google. Первое чтение создаёт исходную точку. Разница балансов может включать покупки, возвраты и расход; тип зачисления не угадывается.",Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,8,0,10)});
        creditActionCheck=AddCheck(body,"При зачислении запускать сохранённый запрос с расходом кредитов");
        dailyOnlyCheck=AddCheck(body,"Выполнять запрос только при сдвиге срока обновления, указанного Google");
        actionPromptBox=AddField(body,"Сохранённый запрос для автоматической генерации","",true);
        actionModelBox=AddField(body,"Модель запроса · выбирается кнопкой ниже");
        body.Children.Add(ActionButton("Использовать выбранную модель и описание из студии",()=>{actionModelBox.Text=ModelKeyBox.Text;actionPromptBox.Text=PromptBox.Text;return Task.CompletedTask;}));
        actionBudgetBox=AddField(body,"Максимум кредитов на одно зачисление","50");actionCountBox=AddField(body,"Максимум генераций на одно зачисление (1–4)","1");
        body.Children.Add(new TextBlock{Text="Если цена выбранной модели неизвестна, доступ не подтверждён или приложение занято, автоматическая генерация пропускается. Один запрос не повторяется после отказа.",Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,10,0,12)});
        body.Children.Add(ActionButton("Сохранить настройки",SavePreferencesAsync,true));
        body.Children.Add(ActionButton("Проверить баланс сейчас",PollCreditsAsync));
        automationStatus=new TextBlock{Text="",Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,12,0,0)};body.Children.Add(automationStatus);
    }
    async Task InitializePreferencesAsync()
    {
        try{preferences=await UserPreferences.LoadAsync();preferences.Validate();}catch{preferences=new();}
        try{remoteMediaState=await ProjectStore.ReadAsync<RemoteMediaState>(RemoteStatePath)??new();}catch{remoteMediaState=new();}
        autoLoginCheck.IsChecked=preferences.AutoLogin;autoDownloadCheck.IsChecked=preferences.AutoDownloadEnabled;autoStitchCheck.IsChecked=preferences.AutoStitchEnabled;downloadFolderBox.Text=preferences.DownloadFolder;
        creditNotifyCheck.IsChecked=preferences.CreditNotificationsEnabled;pollMinutesBox.Text=preferences.CreditPollingMinutes.ToString();creditActionCheck.IsChecked=preferences.CreditActionsEnabled;dailyOnlyCheck.IsChecked=preferences.CreditActionsDailyRefreshOnly;
        actionPromptBox.Text=preferences.CreditActionPrompt;actionModelBox.Text=preferences.CreditActionModelKey;actionBudgetBox.Text=preferences.CreditActionMaximumCredits.ToString(CultureInfo.InvariantCulture);actionCountBox.Text=preferences.CreditActionMaximumGenerations.ToString();
        encoderBox.SelectedIndex=preferences.PreferredEncoder switch{"nvenc"=>1,"amf"=>2,"qsv"=>3,"cpu"=>4,_=>0};cpuCheck.IsChecked=preferences.AllowSoftwareEncoding;
        notifications=new DesktopNotificationService(new WindowInteropHelper(this).Handle);
        creditTimer.Interval=TimeSpan.FromMinutes(preferences.CreditPollingMinutes);creditTimer.Tick+=async(_,_)=>await PollCreditsAsync();creditTimer.Start();
    }
    async Task SavePreferencesAsync()
    {
        var next=new UserPreferences
        {
            AutoLogin=autoLoginCheck.IsChecked==true,AutoDownloadEnabled=autoDownloadCheck.IsChecked==true,AutoStitchEnabled=autoStitchCheck.IsChecked==true,DownloadFolder=downloadFolderBox.Text.Trim(),CreditNotificationsEnabled=creditNotifyCheck.IsChecked==true,
            CreditPollingMinutes=int.TryParse(pollMinutesBox.Text,out var minutes)?minutes:0,CreditActionsEnabled=creditActionCheck.IsChecked==true,CreditActionsDailyRefreshOnly=dailyOnlyCheck.IsChecked==true,
            CreditActionPrompt=actionPromptBox.Text,CreditActionModelKey=actionModelBox.Text.Trim(),CreditActionKind=flowModels.FirstOrDefault(m=>m.ModelKey==actionModelBox.Text.Trim())?.Kind??preferences.CreditActionKind,
            CreditActionMaximumCredits=ParseNumber(actionBudgetBox.Text,"Бюджет"),CreditActionMaximumGenerations=int.TryParse(actionCountBox.Text,out var count)?count:0,
            PreferredEncoder=encoderBox.SelectedIndex switch{1=>"nvenc",2=>"amf",3=>"qsv",4=>"cpu",_=>"auto"},AllowSoftwareEncoding=cpuCheck.IsChecked==true
        };
        next.Validate();await next.SaveAsync();preferences=next;creditTimer.Interval=TimeSpan.FromMinutes(next.CreditPollingMinutes);automationStatus.Text="Настройки сохранены. Новые видео и зачисления будут обрабатываться по этим правилам.";Status("Автоматизация настроена");
    }
    async Task RestoreAccountAsync(CancellationToken ct)
    {
        try{await RefreshFlowAccountAsync(ct);}
        catch(FlowCatalogException ex) when((int?)ex.StatusCode==401&&preferences.AutoLogin&&flowAuth.Session!=null)
        {
            await RestoreSavedSessionAsync(ct);await RefreshFlowAccountAsync(ct);
        }
        if(creditBalance!=null)await ObserveCreditBalanceAsync(creditBalance,ct);
        await RefreshCloudLibraryAsync(ct);
    }
    string MonitorAccountKey()=>flowAuth.Session is {} session?session.Cookies.FirstOrDefault(c=>c.Name=="__Secure-1PAPISID")?.Value??session.Cookies.FirstOrDefault(c=>FlowBrowserLoginService.IsSessionCookie(c.Name))?.Value??"session":FlowCredential;
    async Task RestoreSavedSessionAsync(CancellationToken ct)
    {
        await reconnectGate.WaitAsync(ct);
        try
        {
            if(flowAuth.Session is not {} previous)throw new FlowBrowserLoginException("Сначала выполните вход Google.");
            var revision=accountRevision;lastAutoRestore=DateTimeOffset.UtcNow;FlowProbeText.Text="Восстанавливаем сохранённый вход Google…";
            FlowBrowserSession refreshed;
            try{refreshed=await FlowSessionRefreshService.RefreshAsync(previous,ct);}
            catch(FlowBrowserLoginException){refreshed=await loginBrowser.RestoreAsync(new Progress<string>(s=>{FlowProbeText.Text=s;Status(s);}),ct);}
            if(revision!=accountRevision)throw new OperationCanceledException();
            accountRevision++;flowAuth.Session=refreshed;
            if(RememberToken.IsChecked==true)await secrets.SaveStringAsync("flow-browser-session",System.Text.Json.JsonSerializer.Serialize(refreshed),ct);
        }
        finally{reconnectGate.Release();}
    }
    async Task EnsureGenerationSessionAsync(CancellationToken ct)
    {
        try{creditBalance=await new FlowAccountService(http).GetCreditsAsync(FlowCredential,ct);}
        catch(FlowAccountException ex) when((int?)ex.StatusCode==401&&preferences.AutoLogin&&flowAuth.Session!=null)
        {await RestoreSavedSessionAsync(ct);creditBalance=await new FlowAccountService(http).GetCreditsAsync(FlowCredential,ct);}
        if(RememberToken.IsChecked==true&&flowAuth.Session!=null)await secrets.SaveStringAsync("flow-browser-session",System.Text.Json.JsonSerializer.Serialize(flowAuth.Session),ct);
    }
    async Task PollCreditsAsync()
    {
        if(creditPolling||closePending||loginInProgress||!HasFlowCredential)return;creditPolling=true;var revision=accountRevision;
        try
        {
            var balance=await new FlowAccountService(http).GetCreditsAsync(FlowCredential,lifetime.Token);
            if(revision!=accountRevision)return;creditBalance=balance;CreditBalanceText.Text=$"Кредиты Flow: {(balance.FlowCredits??balance.TotalCredits)?.ToString("N0")??"—"}";ConnectionHint.Text=CreditBalanceText.Text;
            await ObserveCreditBalanceAsync(balance,lifetime.Token);automationStatus.Text=$"Баланс проверен {DateTime.Now:HH:mm}. {CreditBalanceText.Text}";
            if(RememberToken.IsChecked==true&&flowAuth.Session!=null)await secrets.SaveStringAsync("flow-browser-session",System.Text.Json.JsonSerializer.Serialize(flowAuth.Session),lifetime.Token);
            if(!generating&&!preparingGeneration&&!exporting)await RefreshCloudLibraryAsync(lifetime.Token);
        }
        catch(OperationCanceledException){}
        catch(FlowAccountException ex) when((int?)ex.StatusCode==401&&preferences.AutoLogin&&flowAuth.Session!=null&&!generating&&!preparingGeneration&&DateTimeOffset.UtcNow-lastAutoRestore>TimeSpan.FromMinutes(30))
        {try{await RestoreAccountAsync(lifetime.Token);}catch(Exception error){automationStatus.Text="Нужен повторный вход Google: "+error.Message;}}
        catch(Exception ex){automationStatus.Text="Проверка кредитов: "+ex.Message;}
        finally{creditPolling=false;}
    }
    async Task ObserveCreditBalanceAsync(FlowCreditBalance balance,CancellationToken ct)
    {
        var observedRevision=accountRevision;var action=preferences;var credential=FlowCredential;var targetProject=CloudProjectBox.Text;var targetPath=projectPath;
        var change=await creditMonitor.ObserveAsync(balance,MonitorAccountKey(),DateTimeOffset.UtcNow,ct);
        if(observedRevision!=accountRevision)return;
        if(change==null)return;
        if(preferences.CreditNotificationsEnabled)notifications?.Show("Flow · зачисление кредитов",change.Message);
        Status(change.Message);automationStatus.Text=change.Message;
        if(!preferences.CreditActionsEnabled||preferences.CreditActionsDailyRefreshOnly&&!change.DailyRefreshObserved)return;
        if(generating||preparingGeneration||exporting||projectOperation.CurrentCount==0){automationStatus.Text+=" Автогенерация пропущена: приложение занято.";return;}
        var model=flowModels.FirstOrDefault(m=>m.ModelKey==preferences.CreditActionModelKey);
        var price=model?.CreditsForTier(balance.ModelUserTier);
        if(model==null||price is null||price<0||price*preferences.CreditActionMaximumGenerations>preferences.CreditActionMaximumCredits||price*preferences.CreditActionMaximumGenerations>change.NewBalance)
        {automationStatus.Text+=" Автогенерация пропущена: нет подтверждённой цены в пределах бюджета.";return;}
        if(!await creditMonitor.ClaimActionAsync(change.Id,ct))return;
        if(observedRevision!=accountRevision||generating||preparingGeneration||exporting||closePending)return;
        if(!await projectOperation.WaitAsync(0,ct))return;
        // Claim before any submit: crashes and ambiguous failures never resubmit a paid operation.
        var revision=observedRevision;
        generating=true;generationCancellation=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);generationCompletion=completion;
        GenerateButton.Content="Остановить ожидание  ■";
        try
        {
            for(var i=0;i<action.CreditActionMaximumGenerations;i++)
            {
                var request=new FlowGenerationRequest(targetProject,model.ModelKey,action.CreditActionPrompt,model.Kind,"16:9",1,OutputResolution:model.Kind=="video"?(model.Resolutions?.Contains("360p")==true?4:1):null);
                if(string.IsNullOrWhiteSpace(request.ProjectId)){automationStatus.Text+=" Выберите облачный проект в студии.";return;}
                generationCancellation.Token.ThrowIfCancellationRequested();if(revision!=accountRevision)return;
                var files=UseServiceBrowser?await GenerateThroughBrowserAsync(request,ProjectStore.MediaDirectory(targetPath),null,generationCancellation.Token):await new FlowGenerationService(http,mediaHttp).GenerateAsync(request,credential,ProjectStore.MediaDirectory(targetPath),null,generationCancellation.Token);
                if(revision!=accountRevision)return;
                foreach(var file in files)assets.Add(new(){Id=FlowCloudCache.GetAssetId(targetProject,file.MediaName),Path=file.LocalPath,Name=request.Prompt.Length>100?request.Prompt[..100]:request.Prompt,Kind=file.Kind,Prompt=request.Prompt,Model=model.ModelKey,IsGenerated=true,CloudMediaName=file.MediaName,CloudProjectId=targetProject,Thumbnail=ProjectStore.Thumbnail(file.LocalPath)});
                try{await OnGeneratedMediaAsync(files,request,generationCancellation.Token);}catch(Exception ex) when(ex is not OperationCanceledException){automationStatus.Text="Генерация готова; автосохранение/монтаж: "+ex.Message;}MarkDirty();await SaveCurrentAsync();
            }
        }
        catch(Exception ex){automationStatus.Text="Автогенерация остановлена: "+ex.Message;}
        finally{generating=false;GenerateButton.Content="Создать  ↗";generationCancellation.Dispose();generationCancellation=null;projectOperation.Release();completion.TrySetResult(true);}
    }
    async Task OnGeneratedMediaAsync(IReadOnlyList<FlowGeneratedMedia> files,FlowGenerationRequest request,CancellationToken ct)
    {
        foreach(var file in files.Where(f=>f.Kind=="video"))
        {
            if(preferences.AutoDownloadEnabled)await autoDownloads.SaveVideoAsync(file.LocalPath,file.MediaName,preferences.DownloadFolder,ct);
            if(preferences.AutoStitchEnabled)
            {
                var asset=assets.FirstOrDefault(a=>a.CloudMediaName==file.MediaName)??new Asset{Id=FlowCloudCache.GetAssetId(request.ProjectId,file.MediaName),Name=request.Prompt,Path=file.LocalPath,Kind="video",IsGenerated=true,CloudMediaName=file.MediaName,CloudProjectId=request.ProjectId};
                if(!assets.Contains(asset))assets.Add(asset);if(!shots.Any(s=>s.CloudMediaName==file.MediaName))shots.Add(CreateShot(asset));MarkDirty();UpdateCounts();
            }
        }
        if(preferences.AutoStitchEnabled&&files.Any(f=>f.Kind=="video"))
        {
            if(shots.Any(a=>a.Kind!="video")){Status("Автосклейка пропущена: в монтаже есть изображения");return;}
            foreach(var clip in shots)await EnsureCloudAssetAsync(clip,ct);
            Directory.CreateDirectory(preferences.DownloadFolder);
            var output=Path.Combine(preferences.DownloadFolder,SafeFileName(projectName)+"-монтаж-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..4]+".mp4");
            await exporter.ExportTimelineAsync(shots.Select(a=>new MediaTimelineClip(a.Path,a.TrimStartSeconds,a.TrimEndSeconds,a.Volume,a.Muted)).ToArray(),output,TimelineOptions(),null,ct);Status("Автосклейка сохранена в папку видео");
        }
    }
    async Task DownloadNewRemoteVideosAsync(CancellationToken ct)
    {
        var complete=cloudAssets.Where(a=>a.Kind=="video"&&cloudMedia.TryGetValue(a.Id,out var m)&&m.Status=="success").ToArray();
        if(!remoteMediaState.Established)
        {
            foreach(var asset in complete)remoteMediaState.Completed.Add(asset.Id);
            remoteMediaState.Established=true;await ProjectStore.SaveAsync(RemoteStatePath,remoteMediaState);return;
        }
        var options=preferences;
        foreach(var asset in complete.Where(a=>!remoteMediaState.Completed.Contains(a.Id)))
        {
            ct.ThrowIfCancellationRequested();
            if(options.AutoDownloadEnabled)
            {
                try{await EnsureCloudAssetAsync(asset,ct);await autoDownloads.SaveVideoAsync(asset.Path,asset.CloudMediaName,options.DownloadFolder,ct);}
                catch(Exception ex) when(ex is not OperationCanceledException){automationStatus.Text="Новое видео сохранено в Flow; скачивание: "+ex.Message;continue;}
            }
            remoteMediaState.Completed.Add(asset.Id);
        }
        await ProjectStore.SaveAsync(RemoteStatePath,remoteMediaState);
    }
}
