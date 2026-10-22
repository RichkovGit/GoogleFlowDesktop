using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using FlowStudio.Services;

namespace FlowStudio;
public partial class MainWindow : Window
{
    readonly ObservableCollection<Asset> assets = [];
    readonly ObservableCollection<Asset> shots = [];
    readonly ObservableCollection<Job> jobs = [];
    readonly List<string> references = [];
    string? lastFrame;
    readonly SecureStorage secrets = new();
    readonly HardwareService hardware = new();
    readonly FlowBrowserLoginService loginBrowser = new();
    readonly MediaExportService exporter = new();
    readonly FlowCookieAuthHandler flowAuth = new(new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false });
    readonly HttpClient http;
    readonly HttpClient mediaHttp = new(new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false }) { Timeout=TimeSpan.FromMinutes(20) };
    readonly List<FlowCatalogModel> flowModels = [];
    readonly SemaphoreSlim saveGate = new(1);
    readonly SemaphoreSlim projectOperation = new(1,1);
    readonly CancellationTokenSource lifetime = new();
    readonly DispatcherTimer autosave = new() { Interval=TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer playback = new() { Interval=TimeSpan.FromMilliseconds(500) };
    StudioSettings settings = new();
    string projectPath = "";
    string projectName = "Новая история";
    Asset? selectedAsset;
    bool initialized, dirty, closing, closePending, playing, exporting;
    bool generating;
    CancellationTokenSource? generationCancellation;
    CancellationTokenSource? cloudCreationCancellation;
    TaskCompletionSource<bool>? generationCompletion;
    CancellationTokenSource? loginCancellation;
    TaskCompletionSource<bool>? loginCompletion;
    bool loginInProgress;
    bool preparingGeneration;
    CancellationTokenSource? preparationCancellation;
    TaskCompletionSource<bool>? preparationCompletion;
    FlowCreditBalance? creditBalance;
    int accountRevision;
    CancellationTokenSource? exportCancellation;
    TaskCompletionSource<bool>? exportCompletion;
    FlowSessionProbeResult? flowStatus;
    string page = "studio";
    readonly bool uiSmokeTest;

    public MainWindow():this(false){}
    internal MainWindow(bool uiSmokeTest)
    {
        this.uiSmokeTest=uiSmokeTest;
        http=new HttpClient(flowAuth) { Timeout=TimeSpan.FromMinutes(5) };
        InitializeComponent();
        LibraryList.ItemsSource=assets;
        StoryboardList.ItemsSource=shots;
        JobsList.ItemsSource=jobs;
        InitializeExtraUi();InitializeLibraryActions();InitializeReferenceUi();InitializeManagementUi();InitializeBrowserUi();InitializeUpdatesUi();
        autosave.Tick += async (_,_) => { if(dirty && initialized) await SaveCurrentAsync(); };
        playback.Tick += (_,_) => { if(VideoPreview.NaturalDuration.HasTimeSpan && selectedAsset?.Kind=="video") PreviewInfo.Text=$"{VideoPreview.Position:mm\\:ss} / {VideoPreview.NaturalDuration.TimeSpan:mm\\:ss} · {VideoPreview.NaturalVideoWidth} × {VideoPreview.NaturalVideoHeight}"; };
        Loaded += async (_,_) => {await InitializeAsync();if(Environment.GetCommandLineArgs().Contains("--verify-browser"))await RunBrowserLiveVerificationAsync();};
    }

    async Task InitializeAsync()
    {
        try
        {
            string? startupWarning=null;
            try { settings = await ProjectStore.ReadAsync<StudioSettings>(SettingsPath) ?? new(); }
            catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException)
            {
                settings=new();startupWarning="Настройки не прочитаны; используются стандартные. "+ex.Message;
            }
            if(settings.PreferDiscrete) hardware.PreferHighPerformanceGpuForCurrentExecutable();
            if(File.Exists(settings.LastProject))
            {
                try { await LoadProjectAsync(settings.LastProject); }
                catch(Exception ex) when(ex is IOException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
                {
                    startupWarning="Последний проект не открыт; исходный файл сохранён. "+ex.Message;
                }
            }
            if(string.IsNullOrWhiteSpace(projectPath)) { projectPath=ProjectStore.CreateProject(projectName); await SaveCurrentAsync(); }
            try { var token=await secrets.ReadStringAsync("flow-oauth"); if(!string.IsNullOrEmpty(token)) { FlowTokenBox.Password=token; RememberToken.IsChecked=true; } }
            catch(System.Security.Cryptography.CryptographicException) { FlowProbeText.Text="Сохранённый токен недоступен для этого пользователя Windows. Введите новый."; }
            try
            {
                var saved=await secrets.ReadStringAsync("flow-browser-session");
                if(!string.IsNullOrWhiteSpace(saved))
                {
                    var session=JsonSerializer.Deserialize<FlowBrowserSession>(saved);
                    if(session!=null){flowAuth.Session=session;RememberToken.IsChecked=true;FlowProbeText.Text="Сессия Google загружена. Проверяем доступ к Flow…";}
                }
            }
            catch(Exception ex) when(ex is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
            {FlowProbeText.Text="Не удалось восстановить вход. Войдите в Google заново.";}
            initialized=true;
            await InitializePreferencesAsync();
            UpdateCounts();
            UpdateStorage();
            autosave.Start();
            playback.Start();
            Status(startupWarning??"Проект открыт · изменения сохраняются автоматически");
            var snapshot=await hardware.GetSnapshotAsync(RenderCapability.Tier >> 16,lifetime.Token);
            GpuSidebar.Text=snapshot.PreferredGpuName.Replace("NVIDIA GeForce ","") + " · " + (snapshot.HasNvidia ? "NVENC" : "GPU");
            HardwareDetails.Text=string.Join("\n",snapshot.Adapters.Select(a=>$"{a.Name} · {(a.DedicatedMemoryBytes is ulong bytes ? (bytes/1024d/1024d/1024d).ToString("0.#")+" ГБ" : "VRAM неизвестна")}")) + "\n"+snapshot.RenderingDescription+"\nПредпочтение дискретной GPU: "+(snapshot.HighPerformancePreferenceEnabled?"включено":"не включено");
            if(Environment.GetCommandLineArgs().Contains("--login")) {SwitchPage("settings");await LoginFlowAsync();}
            else if(flowAuth.Session!=null)
            {
                try{await RestoreAccountAsync(lifetime.Token);}catch(Exception ex) when(ex is not OperationCanceledException){FlowProbeText.Text=ex.Message;ConnectionHint.Text="Сессия требует проверки. Откройте аккаунт Flow.";}
            }
            _=CheckAppUpdatesAsync();
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { Error(ex); initialized=true; }
    }

    static string SettingsPath => Path.Combine(ProjectStore.AppDirectory,"settings.json");
    void Status(string message) { StatusText.Text=message; }
    void Error(Exception ex) { Status("Операция не завершена"); MessageBox.Show(this,ex.Message,"Flow Studio",MessageBoxButton.OK,MessageBoxImage.Warning); }
    void MarkDirty() { if(!initialized)return; dirty=true; SavedMark.Text="  ·  сохранение…"; }
    StudioProject Snapshot(bool relative=true) => new()
    {
        Name=projectName, Draft=PromptBox.Text, Updated=DateTime.Now,VariantCount=generationCountBox?.SelectedItem is int count?count:1,
        ReferencePaths=references.Select(p=>relative?Path.GetRelativePath(Path.GetDirectoryName(projectPath)!,p):p).ToList(),LastFramePath=lastFrame==null?null:relative?Path.GetRelativePath(Path.GetDirectoryName(projectPath)!,lastFrame):lastFrame,
        Mode=InputMode, AspectRatio=(AspectBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"16:9",
        Seed=SeedBox.Text,CloudProjectId=CloudProjectBox.Text,FlowModelKey=ModelKeyBox.Text,
        Assets=assets.Select(a=>new Asset { Id=a.Id,Name=a.Name,Path=string.IsNullOrWhiteSpace(a.Path)?"":relative?Path.GetRelativePath(Path.GetDirectoryName(projectPath)!,a.Path):a.Path,Kind=a.Kind,Prompt=a.Prompt,Model=a.Model,Created=a.Created,IsGenerated=a.IsGenerated,CloudMediaName=a.CloudMediaName,CloudProjectId=a.CloudProjectId,CloudProjectTitle=a.CloudProjectTitle,CloudWorkflowId=a.CloudWorkflowId,CloudCollectionId=a.CloudCollectionId,IsFavorite=a.IsFavorite,IsTrashed=a.IsTrashed }).ToList(),
        Storyboard=shots.Select(a=>a.Id).ToList(),StoryboardEdits=shots.Select(a=>new ClipEdit(a.TrimStartSeconds,a.TrimEndSeconds,a.Volume,a.Muted)).ToList(), Jobs=jobs.ToList()
    };
    async Task<bool> SaveCurrentAsync()
    {
        if(string.IsNullOrWhiteSpace(projectPath)) return false;
        await saveGate.WaitAsync();
        try
        {
            // Typing may continue while disk I/O is awaiting; persist every newer revision
            // before reporting success to an operation that replaces or closes the project.
            do
            {
                var snapshot=Snapshot();
                dirty=false;
                await ProjectStore.SaveAsync(projectPath,snapshot);
                settings.LastProject=projectPath;
                await ProjectStore.SaveAsync(SettingsPath,settings);
            } while(dirty);
            ProjectHeader.Text=projectName;
            SavedMark.Text="  ·  сохранено";
            return true;
        }
        catch(Exception ex) { dirty=true; SavedMark.Text="  ·  ошибка сохранения"; Status("Не удалось сохранить: "+ex.Message); return false; }
        finally { saveGate.Release(); }
    }

    async Task LoadProjectAsync(string file)
    {
        if(new FileInfo(file).Length>32*1024*1024) throw new InvalidOperationException("Файл проекта слишком велик.");
        var project=await ProjectStore.ReadAsync<StudioProject>(file) ?? throw new InvalidOperationException("Не удалось прочитать проект.");
        if(project.Version!=1) throw new InvalidOperationException("Эта версия проекта не поддерживается.");
        if(project.Assets is null || project.Storyboard is null || project.Jobs is null || project.Name is null || project.Draft is null)
            throw new InvalidOperationException("В файле проекта отсутствуют обязательные поля.");
        if(project.Assets.Count>20000 || project.Storyboard.Count>20000 || project.Jobs.Count>20000) throw new InvalidOperationException("В проекте слишком много материалов.");
        var projectDirectory=Path.GetDirectoryName(Path.GetFullPath(file))!;
        var resolved=new List<Asset>();
        var assetIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var asset in project.Assets)
        {
            if(asset is null || string.IsNullOrWhiteSpace(asset.Id) || !assetIds.Add(asset.Id)
                || (string.IsNullOrWhiteSpace(asset.Path)&&string.IsNullOrWhiteSpace(asset.CloudMediaName)) || asset.Name is null || asset.Prompt is null || asset.Model is null
                || asset.Kind is not ("image" or "video"))
                throw new InvalidOperationException("Файл проекта содержит некорректный материал.");
            if(!string.IsNullOrWhiteSpace(asset.Path))asset.Path=Path.GetFullPath(asset.Path,projectDirectory);
            if(asset.Path.StartsWith(@"\\",StringComparison.Ordinal))
                throw new InvalidOperationException("Проект должен ссылаться на локальные материалы.");
            asset.Thumbnail=ProjectStore.Thumbnail(asset.Path);
            resolved.Add(asset);
        }
        if(project.Storyboard.Any(id=>string.IsNullOrWhiteSpace(id)||!assetIds.Contains(id)))
            throw new InvalidOperationException("Раскадровка ссылается на неизвестный материал.");
        foreach(var job in project.Jobs)
        {
            if(job is null || job.Status is null || job.Prompt is null || job.Model is null)
                throw new InvalidOperationException("Файл проекта содержит некорректную задачу.");
            if(job.Status.StartsWith("В процессе") || job.Status=="В очереди") job.Status="Прервана при закрытии приложения";
        }
        if(project.ReferencePaths?.Count>3)throw new InvalidOperationException("Слишком много референсов в проекте.");
        string ResolveReference(string value)
        {
            var path=Path.GetFullPath(value,projectDirectory);
            if(path.StartsWith(@"\\",StringComparison.Ordinal))throw new InvalidOperationException("Референсы должны храниться локально.");
            return path;
        }
        var resolvedReferences=(project.ReferencePaths??[]).Select(ResolveReference).ToArray();
        var resolvedLastFrame=string.IsNullOrWhiteSpace(project.LastFramePath)?null:ResolveReference(project.LastFramePath);
        // Everything above is validated before replacing any live state.
        initialized=false;
        VideoPreview.Stop();
        projectPath=Path.GetFullPath(file); projectName=project.Name;
        assets.Clear(); shots.Clear(); jobs.Clear(); references.Clear(); lastFrame=null;
        foreach(var asset in resolved) assets.Add(asset);
        for(var i=0;i<project.Storyboard.Count;i++) { var a=assets.FirstOrDefault(a=>a.Id==project.Storyboard[i]); if(a!=null)shots.Add(CreateShot(a,project.StoryboardEdits?.ElementAtOrDefault(i))); }
        foreach(var job in project.Jobs) jobs.Add(job);
        PromptBox.Text=project.Draft;
        ModeBox.SelectedIndex=project.Mode switch{"image"=>1,"frames"=>2,"ingredients"=>3,_=>0};
        generationCountBox.SelectedItem=Math.Clamp(project.VariantCount,1,4);
        references.AddRange(resolvedReferences);lastFrame=resolvedLastFrame;
        AspectBox.SelectedIndex=project.AspectRatio switch {"9:16"=>1,"1:1"=>2,"4:3"=>3,"3:4"=>4,_=>0};
        SeedBox.Text=project.Seed??"";CloudProjectBox.Text=project.CloudProjectId??"";ModelKeyBox.Text=project.FlowModelKey??"";
        ProjectHeader.Text=projectName;
        initialized=true; dirty=false;
        SelectAsset(null); UpdateCounts(); UpdateReferences(); UpdateStorage();RefreshComposerModels();
        Status("Открыт проект «"+projectName+"»");
    }

    async void NewProject_Click(object sender,RoutedEventArgs e)
    {
        if(closePending)return;
        if(exporting||generating||preparingGeneration) { Status("Дождитесь завершения задачи или отмените её через Esc"); return; }
        var name=ProjectNameDialog("Новый проект","Название вашей истории", "Новая история");
        if(name==null)return;
        if(!await projectOperation.WaitAsync(0)) { Status("Дождитесь завершения работы с проектом"); return; }
        try
        {
            if(!string.IsNullOrWhiteSpace(projectPath) && !await SaveCurrentAsync())return;
            var newFile=ProjectStore.CreateProject(name);
            // Verify the new destination before clearing the current project.
            await ProjectStore.SaveAsync(newFile,new StudioProject{Name=name});
            initialized=false;
            assets.Clear(); shots.Clear(); jobs.Clear(); references.Clear(); lastFrame=null;
            PromptBox.Clear(); projectName=name; projectPath=newFile;
            SeedBox.Clear();CloudProjectBox.Clear();ModelKeyBox.Clear();
            initialized=true;
            SelectAsset(null); UpdateCounts(); UpdateReferences(); UpdateStorage(); SwitchPage("studio");
            RefreshComposerModels();
            if(await SaveCurrentAsync())Status("Новый проект готов");
        }
        catch(Exception ex) { Error(ex); }
        finally { projectOperation.Release(); }
    }
    async void OpenProject_Click(object sender,RoutedEventArgs e)
    {
        if(closePending)return;
        if(exporting||generating||preparingGeneration) { Status("Дождитесь завершения задачи или отмените её через Esc"); return; }
        var dialog=new OpenFileDialog{ Filter="Проект Flow Studio|*.flowstudio",InitialDirectory=ProjectStore.ProjectsDirectory };
        if(dialog.ShowDialog(this)!=true)return;
        if(!await projectOperation.WaitAsync(0)) { Status("Дождитесь завершения работы с проектом"); return; }
        try { if(!string.IsNullOrWhiteSpace(projectPath) && !await SaveCurrentAsync())return; await LoadProjectAsync(dialog.FileName); await SaveCurrentAsync(); SwitchPage("studio"); }
        catch(Exception ex) { Error(ex); }
        finally { projectOperation.Release(); }
    }
    async void SaveAs_Click(object sender,RoutedEventArgs e)
    {
        if(closePending)return;
        if(exporting||generating||preparingGeneration) { Status("Дождитесь завершения задачи или отмените её через Esc"); return; }
        var name=ProjectNameDialog("Сохранить копию","Название копии",projectName+" — копия");
        if(name==null)return;
        if(!await projectOperation.WaitAsync(0)) { Status("Дождитесь завершения работы с проектом"); return; }
        try
        {
            // Save a copy remains a recovery path when the original is read-only.
            if(!string.IsNullOrWhiteSpace(projectPath))await SaveCurrentAsync();
            var newFile=ProjectStore.CreateProject(name);
            var copy=Snapshot(relative:false);
            copy.Name=name;
            var copiedPaths=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var asset in copy.Assets)
            {
                if(!File.Exists(asset.Path))
                {if(!string.IsNullOrWhiteSpace(asset.CloudMediaName)){asset.Path="";continue;}throw new FileNotFoundException("Нельзя скопировать проект: материал не найден.",asset.Path);}
                var imported=await ProjectStore.ImportAsync(asset.Path,newFile);
                copiedPaths.Add(asset.Id,imported);
                asset.Path=Path.GetRelativePath(Path.GetDirectoryName(newFile)!,imported);
            }
            var copiedReferences=new List<string>();
            foreach(var path in references)copiedReferences.Add(await ProjectStore.ImportAsync(path,newFile));
            var copiedLastFrame=lastFrame==null?null:await ProjectStore.ImportAsync(lastFrame,newFile);
            copy.ReferencePaths=copiedReferences.Select(p=>Path.GetRelativePath(Path.GetDirectoryName(newFile)!,p)).ToList();
            copy.LastFramePath=copiedLastFrame==null?null:Path.GetRelativePath(Path.GetDirectoryName(newFile)!,copiedLastFrame);
            await ProjectStore.SaveAsync(newFile,copy);
            // Commit only after every file and the new manifest were written successfully.
            foreach(var asset in assets)asset.Path=copiedPaths.GetValueOrDefault(asset.Id)??"";
            foreach(var shot in shots)if(copiedPaths.TryGetValue(shot.Id,out var copied))shot.Path=copied;
            references.Clear();references.AddRange(copiedReferences);lastFrame=copiedLastFrame;UpdateReferences();
            projectName=name; projectPath=newFile;
            if(selectedAsset!=null)SelectAsset(selectedAsset);
            UpdateStorage();
            if(await SaveCurrentAsync())Status("Копия сохранена вместе с материалами");
        }
        catch(Exception ex) { Error(ex); }
        finally { projectOperation.Release(); }
    }
    async void Import_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog { Filter="Изображения и видео|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.mp4;*.mov;*.mkv;*.webm;*.avi",Multiselect=true };
        if(dialog.ShowDialog(this)==true) await ImportFilesAsync(dialog.FileNames);
    }
    async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        if(closePending)return;
        if(string.IsNullOrWhiteSpace(projectPath)) { Status("Сначала создайте или откройте проект");return; }
        if(!await projectOperation.WaitAsync(0)) { Status("Дождитесь завершения работы с проектом"); return; }
        Asset? last=null;
        bool succeeded=false;
        try
        {
            foreach(string file in paths.Where(ProjectStore.IsMedia))
            {
                Status("Импорт: "+Path.GetFileName(file));
                string imported=await ProjectStore.ImportAsync(file,projectPath);
                var a=new Asset {Path=imported,Name=Path.GetFileNameWithoutExtension(file),Kind=ProjectStore.IsVideo(file)?"video":"image",Thumbnail=ProjectStore.Thumbnail(imported)};
                assets.Add(a);last=a;
            }
            succeeded=true;
        }
        catch(Exception ex) { Error(ex); }
        finally
        {
            try
            {
                if(last!=null)
                {
                    UpdateCounts();MarkDirty();
                    bool saved=await SaveCurrentAsync();
                    SelectAsset(last);SwitchPage("studio");
                    if(succeeded && saved)Status("Материалы скопированы в проект");
                }
            }
            finally { projectOperation.Release(); }
        }
    }
    async void Window_Drop(object sender,DragEventArgs e)
    {
        if(e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files) await ImportFilesAsync(files.Where(File.Exists));
    }

    void SelectAsset(Asset? asset)
    {
        selectionRevision++;
        VideoPreview.Stop(); playing=false; selectedAsset=asset;
        ImagePreview.Source=null;VideoPreview.Source=null;
        WelcomeArt.Visibility=asset==null?Visibility.Visible:Visibility.Collapsed;
        ImagePreview.Visibility=asset?.Kind=="image"?Visibility.Visible:Visibility.Collapsed;
        VideoPreview.Visibility=asset?.Kind=="video"?Visibility.Visible:Visibility.Collapsed;
        PreviewBadge.Visibility=asset==null?Visibility.Collapsed:Visibility.Visible;
        PlayButton.IsEnabled=asset?.Kind=="video";
        PlayButton.Content="▶";
        PreviewName.Text=asset?.Name??"Холст проекта";
        PreviewInfo.Text=asset==null?"Изображения и видео · предпросмотр на устройстве":asset.Kind=="image"?"Локальное изображение":"Локальное видео · нажмите ▶";
        PreviewKind.Text=asset?.IsGenerated==true?"FLOW · ГЕНЕРАЦИЯ":asset is null?"":asset.Caption+" · ИМПОРТ";
        VideoPreview.IsMuted=LibraryPreviewMuted;
        if(asset==null)return;
        if(!File.Exists(asset.Path)) { PlayButton.IsEnabled=false;if(!string.IsNullOrEmpty(asset.CloudMediaName)){PreviewInfo.Text="Скачиваем оригинал из Flow…";previewTask=LoadCloudPreviewAsync(asset,selectionRevision);}else{PreviewInfo.Text="Файл материала не найден";Status("Файл материала не найден: "+asset.Name);}return; }
        if(asset.Kind=="image")
        {
            // Decode to preview size; the original on disk remains unchanged.
            ImagePreview.Source=ProjectStore.Thumbnail(asset.Path,2048);
            if(ImagePreview.Source is System.Windows.Media.Imaging.BitmapSource b) PreviewInfo.Text=$"{Path.GetExtension(asset.Path).TrimStart('.').ToUpperInvariant()} · {new FileInfo(asset.Path).Length/1024d:0} КБ";
            else PreviewInfo.Text="Windows не смог открыть этот кодек изображения. Оригинал сохранён в проекте.";
        }
        else { VideoPreview.Source=new Uri(asset.Path); VideoPreview.Play(); VideoPreview.Pause(); }
    }
    void Asset_SelectionChanged(object sender,SelectionChangedEventArgs e) { if(LibraryList.SelectedItem is Asset a) SelectAsset(a); }
    void Library_DoubleClick(object sender,MouseButtonEventArgs e) { if(LibraryList.SelectedItem is Asset a) { SelectAsset(a); SwitchPage("studio"); } }
    void Shot_SelectionChanged(object sender,SelectionChangedEventArgs e) { if(StoryboardList.SelectedItem is Asset a) { SelectAsset(a); SwitchPage("studio"); } }
    void Play_Click(object sender,RoutedEventArgs e) { if(selectedAsset?.Kind!="video")return; playing=!playing; if(playing)VideoPreview.Play(); else VideoPreview.Pause(); PlayButton.Content=playing?"Ⅱ":"▶"; }
    void Video_MediaOpened(object sender,RoutedEventArgs e) { if(VideoPreview.NaturalDuration.HasTimeSpan) PreviewInfo.Text=$"{VideoPreview.NaturalDuration.TimeSpan:mm\\:ss} · {VideoPreview.NaturalVideoWidth} × {VideoPreview.NaturalVideoHeight}"; }
    void Video_MediaEnded(object sender,RoutedEventArgs e) { VideoPreview.Pause(); VideoPreview.Position=TimeSpan.Zero; playing=false;PlayButton.Content="▶"; }
    void Video_MediaFailed(object sender,ExceptionRoutedEventArgs e) { playing=false;PlayButton.Content="▶";PreviewInfo.Text="Windows не смог декодировать видео. Попробуйте MP4 / H.264."; }

    async void AddShot_Click(object sender,RoutedEventArgs e) { if(exporting)return;if(selectedAsset==null) { SwitchPage("library"); Status("Сначала выберите материал в медиатеке"); return; }try{var asset=selectedAsset;await EnsureCloudAssetAsync(asset,lifetime.Token);if(!assets.Any(a=>a.Id==asset.Id))assets.Add(asset);shots.Add(CreateShot(asset));UpdateCounts();MarkDirty();Status("Сцена добавлена в раскадровку");}catch(Exception ex){Error(ex);} }
    void RemoveShot_Click(object sender,RoutedEventArgs e) { if(exporting)return;int i=StoryboardList.SelectedIndex; if(i>=0) { shots.RemoveAt(i);UpdateCounts();MarkDirty(); } }
    void MoveLeft_Click(object sender,RoutedEventArgs e) => MoveShot(-1);
    void MoveRight_Click(object sender,RoutedEventArgs e) => MoveShot(1);
    void MoveShot(int direction) { if(exporting)return;int i=StoryboardList.SelectedIndex;int j=i+direction;if(i<0||j<0||j>=shots.Count)return;shots.Move(i,j);StoryboardList.SelectedIndex=j;MarkDirty(); }
    void UpdateCounts()
    {
        AssetCount.Text=$"{assets.Count} {Plural(assets.Count,"материал","материала","материалов")}".ToUpperInvariant();ShotCount.Text=$"{shots.Count} {Plural(shots.Count,"сцена","сцены","сцен")}";
        var visibleCount=libraryScope?.SelectedIndex==0?cloudAssets.Count:assets.Count;
        if(page=="library")AssetCount.Text=$"{visibleCount} {Plural(visibleCount,"материал","материала","материалов")}".ToUpperInvariant();
        EmptyLibrary.Visibility=visibleCount==0?Visibility.Visible:Visibility.Collapsed;
        EmptyLibrary.Text=libraryScope?.SelectedIndex==0?HasFlowCredential?"Обновите медиатеку аккаунта кнопкой сверху.":"Подключите аккаунт Google, чтобы увидеть материалы Flow.":"В текущем проекте пока нет материалов.\nДобавьте их из аккаунта или импортируйте файлы.";
        EmptyStoryboard.Visibility=shots.Count==0?Visibility.Visible:Visibility.Collapsed;
        EmptyQueue.Visibility=jobs.Count==0?Visibility.Visible:Visibility.Collapsed;
        UpdateLibraryFilteredState();
    }
    static string Plural(int count,string one,string few,string many) => count%100 is >=11 and <=14?many:(count%10) switch {1=>one,2 or 3 or 4=>few,_=>many};
    void LibrarySearch_TextChanged(object sender,TextChangedEventArgs e)
    {
        if(LibraryList==null)return;
        var view=System.Windows.Data.CollectionViewSource.GetDefaultView(LibraryList.ItemsSource);
        if(view==null)return;
        view.Filter=o=>o is Asset a && a.Name.Contains(LibrarySearch.Text,StringComparison.OrdinalIgnoreCase);
        ApplyLibraryView();
    }

    void SwitchPage(string target)
    {
        page=target;
        SetExtraPage(target);
        if(target=="agent"&&agentProjectBox.SelectedItem==null)
            agentProjectBox.SelectedItem=cloudProjects.FirstOrDefault(p=>p.Id==CloudProjectBox.Text.Trim().Replace("projects/",""));
        LibraryPage.Visibility=target=="library"?Visibility.Visible:Visibility.Collapsed;
        QueuePage.Visibility=target=="queue"?Visibility.Visible:Visibility.Collapsed;
        SettingsPage.Visibility=target=="settings"?Visibility.Visible:Visibility.Collapsed;
        PageTitle.Text=target switch {"library"=>"Медиатека","queue"=>"Очередь задач",_=>"Студия"};
        UpdateCounts();
        foreach(var (button,key) in new[]{(StudioNav,"studio"),(LibraryNav,"library"),(QueueNav,"queue"),(SettingsNav,"settings")}) { button.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(target==key?"#2B3325":"#0015181A"));button.Foreground=(Brush)FindResource(target==key?"Accent":"Muted"); }
    }
    void Studio_Click(object sender,RoutedEventArgs e)=>SwitchPage("studio");
    void Library_Click(object sender,RoutedEventArgs e)=>SwitchPage("library");
    void Queue_Click(object sender,RoutedEventArgs e)=>SwitchPage("queue");
    void Settings_Click(object sender,RoutedEventArgs e)=>SwitchPage("settings");
    void Mode_Changed(object sender,SelectionChangedEventArgs e) { if(DurationBox==null)return;DurationBox.IsEnabled=false;UpdateReferences();if(initialized)ModelKeyBox.Clear();RefreshComposerModels();MarkDirty(); }
    string InputMode => (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "video";
    string Mode => InputMode=="image"?"image":"video";
    void Prompt_Changed(object sender,TextChangedEventArgs e)=>MarkDirty();
    void Composition_Changed(object sender,SelectionChangedEventArgs e) { if(initialized)RefreshComposerModels();MarkDirty(); }
    void ComposerModel_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(modelSelectionUpdating||ModelKeyBox==null||ComposerModelBox.SelectedItem is not FlowModelFamily family)return;
        var prior=ModelKeyBox.Text;modelSelectionUpdating=true;
        try{DurationBox.ItemsSource=family.Variants;DurationBox.DisplayMemberPath="VariantLabel";DurationBox.IsEnabled=family.Variants.Count>1;DurationBox.SelectedItem=family.Variants.FirstOrDefault(m=>m.ModelKey==prior)??family.Variants.FirstOrDefault();if(DurationBox.SelectedItem is FlowCatalogModel model)ApplySelectedModel(model);}
        finally{modelSelectionUpdating=false;}
    }
    void ApplySelectedModel(FlowCatalogModel model)
    {
        ModelKeyBox.Text=model.ModelKey;
        var price=model.CreditsForTier(creditBalance?.ModelUserTier);
        ConnectionHint.Text=(creditBalance?.FlowCredits is {} balance?$"Кредиты Flow: {balance:N0}. ":"")+(price is {} cost?$"Выбранный вариант: {cost*(generationCountBox?.SelectedItem is int resultCount?resultCount:1):0.##} кредитов.":"Google не указал цену выбранного варианта.");MarkDirty();
    }
    void Variant_Changed(object sender,SelectionChangedEventArgs e)
    {if(!modelSelectionUpdating&&DurationBox.SelectedItem is FlowCatalogModel model)ApplySelectedModel(model);}
    void RefreshComposerModels()
    {
        if(ComposerModelBox==null||DurationBox==null||ModelKeyBox==null)return;
        var key=ModelKeyBox.Text;var ratio=(AspectBox?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var families=FlowModelCatalogService.GetInputFamilies(flowModels,Mode,GenerationInputs,ratio,creditBalance?.ModelUserTier);
        modelSelectionUpdating=true;
        try
        {
            ComposerModelBox.ItemsSource=families;
            ComposerModelBox.SelectedItem=families.FirstOrDefault(f=>f.Variants.Any(m=>m.ModelKey==key))??families.FirstOrDefault();
            var family=ComposerModelBox.SelectedItem as FlowModelFamily;
            DurationBox.ItemsSource=family?.Variants;DurationBox.DisplayMemberPath="VariantLabel";DurationBox.IsEnabled=family?.Variants.Count>1;
            DurationBox.SelectedItem=family?.Variants.FirstOrDefault(m=>m.ModelKey==key)??family?.Variants.FirstOrDefault();
            if(DurationBox.SelectedItem is FlowCatalogModel model)ApplySelectedModel(model);else ModelKeyBox.Clear();
        }
        finally{modelSelectionUpdating=false;}
    }
    void UpdateReferences() { if(ReferenceLabel==null)return;ReferenceLabel.Text=references.Count==0?"Первый кадр или до 3 ингредиентов":string.Join(", ",references.Select(Path.GetFileName));if(lastFrame!=null)ReferenceLabel.Text+="\nПоследний: "+Path.GetFileName(lastFrame); }

    string FlowCredential => flowAuth.Session!=null?FlowCookieAuthHandler.SessionCredentialMarker:FlowTokenBox.Password;
    bool HasFlowCredential => flowAuth.Session!=null||!string.IsNullOrWhiteSpace(FlowTokenBox.Password);
    async void LoginGoogle_Click(object sender,RoutedEventArgs e)
    {
        if(loginInProgress){loginCancellation?.Cancel();return;}
        if(generating||preparingGeneration||cloudCreationCancellation!=null){Status("Завершите текущий запрос перед сменой аккаунта");return;}
        await LoginFlowAsync();
    }
    void ContinueLogin_Click(object sender,RoutedEventArgs e)
    {
        if(loginInProgress)loginBrowser.ConfirmManualSignIn();
        ContinueLoginButton.IsEnabled=false;
    }
    async Task<bool> LoginFlowAsync()
    {
        if(closePending||loginInProgress)return false;
        loginInProgress=true;LoginGoogleButton.Content="Отменить вход";ProbeButton.IsEnabled=false;
        loginCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);loginCompletion=completion;
        try
        {
            await serviceBrowser.StopAsync();
            FlowProbeText.Text="Открываем отдельное окно Chrome. Войдите в Google самостоятельно; пароль не передаётся приложению.";
            var session=await loginBrowser.LoginAsync(new Progress<string>(s=>{FlowProbeText.Text=s;Status(s);ContinueLoginButton.Visibility=loginBrowser.AwaitingManualConfirmation?Visibility.Visible:Visibility.Collapsed;ContinueLoginButton.IsEnabled=loginBrowser.AwaitingManualConfirmation;}),loginCancellation.Token);
            libraryCancellation?.Cancel();if(libraryTask!=null)await libraryTask;
            await CancelLibraryActionsAsync();
            accountRevision++;
            cloudAssets.Clear();cloudProjects.Clear();cloudMedia.Clear();conversation=null;agentHistory.Clear();agentSessions.ItemsSource=null;
            flowAuth.Session=session;
            flowStatus=null;creditBalance=null;flowModels.Clear();RefreshComposerModels();ModelKeyBox.Clear();CreditBalanceText.Text="Проверяем кредиты…";ConnectionHint.Text="Проверяем аккаунт Flow…";
            await RefreshFlowAccountAsync(loginCancellation.Token);
            if(RememberToken.IsChecked==true)await secrets.SaveStringAsync("flow-browser-session",JsonSerializer.Serialize(session),loginCancellation.Token);
            else secrets.Delete("flow-browser-session");
            FlowProbeText.Text="Вход Google выполнен. Каталог Flow проверен нативным запросом. Браузер закрыт.";
            await RefreshCloudLibraryAsync(loginCancellation.Token);
            Status("Аккаунт Flow подключён");
            return true;
        }
        catch(OperationCanceledException){FlowProbeText.Text="Вход остановлен";return false;}
        catch(Exception ex){FlowProbeText.Text=ex.Message;ConnectionHint.Text="Подключение Flow не подтверждено. Откройте настройки аккаунта.";Status("Подключение Flow не подтверждено");return false;}
        finally
        {
            loginInProgress=false;LoginGoogleButton.Content="Войти через Google";LoginGoogleButton.IsEnabled=true;ProbeButton.IsEnabled=true;
            ContinueLoginButton.Visibility=Visibility.Collapsed;
            loginCancellation.Dispose();loginCancellation=null;completion.TrySetResult(true);
        }
    }
    async Task RefreshFlowAccountAsync(CancellationToken cancellationToken)
    {
        if(!HasFlowCredential)throw new InvalidOperationException("Сначала войдите в Google.");
        var revision=accountRevision;
        var credential=FlowCredential;
        using var catalog=new FlowModelCatalogService(http);
        var models=await catalog.ListAsync(credential,cancellationToken);
        if(revision!=accountRevision)throw new OperationCanceledException("Аккаунт изменён");
        flowModels.Clear();flowModels.AddRange(models);RefreshComposerModels();
        if(ComposerModelBox.SelectedItem==null&&ComposerModelBox.Items.Count>0)ComposerModelBox.SelectedIndex=0;
        flowStatus=new(FlowSessionStatus.Reachable,200,"Каталог Flow доступен",models.Count);
        FlowProbeText.Text=$"Аккаунт Flow подключён. Доступно вариантов моделей: {models.Count}.";
        try
        {
            var freshBalance=await new FlowAccountService(http).GetCreditsAsync(credential,cancellationToken);
            if(revision!=accountRevision)throw new OperationCanceledException("Аккаунт изменён");
            creditBalance=freshBalance;
              RefreshComposerModels();
            var flow=creditBalance.FlowCredits;var total=creditBalance.TotalCredits;
            CreditBalanceText.Text=flow is not null?$"Кредиты Flow: {flow:N0}":total is not null?$"Кредиты аккаунта: {total:N0}":"Баланс не указан Google";
            if(flow is not null&&total is not null&&flow!=total)CreditBalanceText.Text+=$"  ·  всего {total:N0}";
            ConnectionHint.Text=CreditBalanceText.Text+". Генерация использует кредиты Google.";
        }
        catch(FlowAccountException ex){if(revision!=accountRevision)throw new OperationCanceledException("Аккаунт изменён");CreditBalanceText.Text="Баланс не получен";ConnectionHint.Text="Каталог Flow доступен. "+ex.Message;}
        if(revision!=accountRevision)throw new OperationCanceledException("Аккаунт изменён");
        if(RememberToken.IsChecked==true)
        {
            if(flowAuth.Session!=null)await secrets.SaveStringAsync("flow-browser-session",JsonSerializer.Serialize(flowAuth.Session),cancellationToken);
            else await secrets.SaveStringAsync("flow-oauth",FlowTokenBox.Password,cancellationToken);
        }
    }

    async void ProbeFlow_Click(object sender,RoutedEventArgs e)
    {
        ProbeButton.IsEnabled=false;
        FlowProbeText.Text="Проверяем каталог Flow…";
        try
        {
            await RestoreAccountAsync(lifetime.Token);
            Status("Каталог и аккаунт Flow обновлены");
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { Error(ex); }
        finally { ProbeButton.IsEnabled=true; }
    }
    async void ForgetToken_Click(object sender,RoutedEventArgs e)
    {
        await CancelLibraryActionsAsync();
        accountRevision++;
        libraryCancellation?.Cancel();
        loginCancellation?.Cancel();generationCancellation?.Cancel();preparationCancellation?.Cancel();
        cloudCreationCancellation?.Cancel();
        if(loginCompletion is {} loginDone)await loginDone.Task;
        if(preparationCompletion is {} preparationDone)await preparationDone.Task;
        await projectOperation.WaitAsync();
        try
        {
        accountRevision++;
        secrets.Delete("flow-oauth");secrets.Delete("flow-browser-session");flowAuth.Session=null;FlowTokenBox.Clear();flowStatus=null;creditBalance=null;flowModels.Clear();RefreshComposerModels();FlowProbeText.Text="Аккаунт отключён от приложения";CreditBalanceText.Text="Кредиты Flow: —";ConnectionHint.Text="Подключите аккаунт Flow в настройках.";
        cloudAssets.Clear();cloudMedia.Clear();cloudProjects.Clear();RefreshLibraryScope();
        }
        finally{projectOperation.Release();}
    }
    async void CreateCloud_Click(object sender,RoutedEventArgs e)
    {
        if(closePending||generating||exporting)return;
        if(!HasFlowCredential) {FlowProbeText.Text="Сначала войдите через Google.";return;}
        if(!await projectOperation.WaitAsync(0))return;
        CreateCloudButton.IsEnabled=false;
        cloudCreationCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            var project=await new FlowGenerationService(http,mediaHttp).CreateProjectAsync(projectName,FlowCredential,cloudCreationCancellation.Token);
            CloudProjectBox.Text=project.Id;MarkDirty();await SaveCurrentAsync();
            FlowProbeText.Text="Google создал облачный проект: "+project.Name;
            Status("Облачный проект Flow создан");
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Error(ex);}
        finally{cloudCreationCancellation.Dispose();cloudCreationCancellation=null;CreateCloudButton.IsEnabled=true;projectOperation.Release();}
    }
    async void Generate_Click(object sender,RoutedEventArgs e)
    {
        if(closePending)return;
        if(generating){generationCancellation?.Cancel();return;}
        if(preparingGeneration){Status("Подготавливаем запрос Flow. Esc — отменить");return;}
        preparingGeneration=true;
        preparationCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);preparationCompletion=completion;
        try{await GenerateCoreAsync(preparationCancellation.Token);}
        catch(OperationCanceledException){Status("Подготовка запроса остановлена");}
        catch(Exception ex){if(!closePending)Error(ex);}
        finally{preparingGeneration=false;preparationCancellation.Dispose();preparationCancellation=null;completion.TrySetResult(true);}
    }
    async Task GenerateCoreAsync(CancellationToken preparationToken)
    {
        if(closePending)return;
        if(generating){generationCancellation?.Cancel();return;}
        if(projectOperation.CurrentCount==0||exporting){Status("Дождитесь завершения текущей операции");return;}
        if(string.IsNullOrWhiteSpace(PromptBox.Text)){PromptBox.Focus();Status("Опишите сцену в поле «Ваша идея»");return;}
        if(!HasFlowCredential)
        {
            SwitchPage("settings");
            if(!await LoginFlowAsync())return;
            preparationToken.ThrowIfCancellationRequested();
        }
        if(string.IsNullOrWhiteSpace(ModelKeyBox.Text))
        {
            if(flowModels.Count==0)try{await RefreshFlowAccountAsync(preparationToken);}catch(OperationCanceledException){throw;}catch(Exception ex){Error(ex);return;}
            if(ComposerModelBox.SelectedItem==null&&ComposerModelBox.Items.Count>0)ComposerModelBox.SelectedIndex=0;
            if(string.IsNullOrWhiteSpace(ModelKeyBox.Text)){Status("Для этого режима нет доступной модели Flow");return;}
        }
        await EnsureGenerationSessionAsync(preparationToken);
        if(string.IsNullOrWhiteSpace(CloudProjectBox.Text))
        {
            try
            {
                var cloud=await new FlowGenerationService(http,mediaHttp).CreateProjectAsync(projectName,FlowCredential,preparationToken);
                CloudProjectBox.Text=cloud.Id;MarkDirty();await SaveCurrentAsync();
            }
            catch(OperationCanceledException){throw;}
            catch(Exception ex){Error(ex);return;}
        }
        int? seed=null;
        if(!string.IsNullOrWhiteSpace(SeedBox.Text)){if(!int.TryParse(SeedBox.Text,out var parsed)||parsed<0){Status("Seed должен быть целым числом от 0 до 2147483647");return;}seed=parsed;}
        if(Mode=="video"&&seed!=null){Status("Seed пока доступен только для изображений");return;}
        var ratio=(AspectBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"16:9";
        if(Mode=="video"&&ratio is not("16:9" or "9:16")){Status("Для видео выберите 16:9 или 9:16");return;}
        var request=new FlowGenerationRequest(CloudProjectBox.Text,ModelKeyBox.Text,PromptBox.Text,Mode,ratio,generationCountBox.SelectedItem is int outputs?outputs:1,string.IsNullOrWhiteSpace(RecaptchaBox.Password)?null:RecaptchaBox.Password,seed,OutputResolution:Mode=="video"?(DurationBox.SelectedItem is FlowCatalogModel chosen&&chosen.Resolutions?.Contains("360p")==true?4:1):null);
        if(UseServiceBrowser&&(references.Count>0||lastFrame!=null))throw new InvalidOperationException("Загрузка референсов через служебный интерфейс ещё не подключена. Выберите прямой способ отправки.");
        request=await AttachReferencesAsync(request,preparationToken);
        var token=FlowCredential;
        RecaptchaBox.Clear(); // These action tokens expire and must never be persisted.
        var job=new Job{Prompt=request.Prompt,Model=request.ModelKey,Status="В процессе · подключение к Flow"};
        jobs.Insert(0,job);UpdateCounts();MarkDirty();
        preparationToken.ThrowIfCancellationRequested();
        generationCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,preparationToken);
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);generationCompletion=completion;
        generating=true;GenerateButton.Content="Остановить ожидание  ■";
        try
        {
            var progress=new Progress<string>(s=>{job.Status="В процессе · "+s;Status(s);});
            var files=UseServiceBrowser?await GenerateThroughBrowserAsync(request,ProjectStore.MediaDirectory(projectPath),progress,generationCancellation.Token):await new FlowGenerationService(http,mediaHttp).GenerateAsync(request,token,ProjectStore.MediaDirectory(projectPath),progress,generationCancellation.Token);
            foreach(var file in files)
            {
                var asset=new Asset{Path=file.LocalPath,Name=request.Prompt.Length>55?request.Prompt[..55]+"…":request.Prompt,Kind=file.Kind,Prompt=request.Prompt,Model=request.ModelKey,IsGenerated=true,Thumbnail=ProjectStore.Thumbnail(file.LocalPath),CloudMediaName=file.MediaName,CloudProjectId=request.ProjectId,CloudProjectTitle=projectName,CloudWorkflowId=file.WorkflowId??""};
                assets.Add(asset);SelectAsset(asset);
            }
            try{await OnGeneratedMediaAsync(files,request,generationCancellation.Token);}catch(Exception ex) when(ex is not OperationCanceledException){Status("Генерация готова; автосохранение/монтаж: "+ex.Message);}
            if(LibraryClearPromptAfterSuccess&&PromptBox.Text==request.Prompt)PromptBox.Clear();
            job.Status="Готово · "+files.Count+" результат(ов) Flow";Status("Результат Flow сохранён в проекте");SwitchPage("studio");
            try{creditBalance=await new FlowAccountService(http).GetCreditsAsync(FlowCredential,generationCancellation.Token);CreditBalanceText.Text=$"Кредиты Flow: {(creditBalance.FlowCredits??creditBalance.TotalCredits)?.ToString("N0")??"—"}";ConnectionHint.Text=CreditBalanceText.Text;}catch(FlowAccountException){}
        }
        catch(OperationCanceledException){job.Status="Локальное ожидание остановлено; задание в Google может продолжаться";Status(job.Status);}
        catch(Exception ex){job.Status="Ошибка · "+ex.Message;if(!closePending)Error(ex);}
        finally
        {
            generating=false;GenerateButton.Content="Создать  ↗";generationCancellation.Dispose();generationCancellation=null;
            UpdateCounts();MarkDirty();try{await SaveCurrentAsync();}finally{completion.TrySetResult(true);}
        }
    }
    async void PreferGpu_Click(object sender,RoutedEventArgs e) { var result=hardware.PreferHighPerformanceGpuForCurrentExecutable();settings.PreferDiscrete=result.Success;await ProjectStore.SaveAsync(SettingsPath,settings);Status(result.Message);HardwareDetails.Text+="\n"+result.Message; }
    async void ProbeNvenc_Click(object sender,RoutedEventArgs e) { try{NvencStatus.Text="Проверяем видеокодировщики…";await ProbeAllEncodersAsync();NvencStatus.Text=encoderStatus.Text;}catch(OperationCanceledException){}catch(Exception ex){Error(ex);} }
    void UpdateStorage() { StorageDetails.Text="Проект: "+projectPath+"\nСекреты: Windows DPAPI / текущий пользователь. Автосохранение: каждые 2 секунды."; }
    void OpenFolder_Click(object sender,RoutedEventArgs e) { if(Directory.Exists(Path.GetDirectoryName(projectPath)))Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={Path.GetDirectoryName(projectPath)!},UseShellExecute=true}); }

    async void Export_Click(object sender,RoutedEventArgs e)
    {
        if(closePending)return;
        if(exporting){exportCancellation?.Cancel();Status("Отмена экспорта…");return;}
        try
        {
            if(shots.Any(a=>a.Kind!="video")){await ExportStoryboardAsync();return;}
            SwitchPage("editor");await ExportEditedTimelineAsync();
        }
        catch(OperationCanceledException){Status("Экспорт остановлен");}
        catch(Exception ex){Error(ex);}
    }
    static string SafeFileName(string name) => string.Concat(name.Where(c=>!Path.GetInvalidFileNameChars().Contains(c)));
    async void ExportStoryboard_Click(object sender,RoutedEventArgs e) { try{await ExportStoryboardAsync();}catch(Exception ex){Error(ex);} }
    async Task ExportStoryboardAsync()
    {
        var dialog=new SaveFileDialog{Filter="Раскадровка JSON|*.json",FileName=SafeFileName(projectName)+"-storyboard.json"};
        if(dialog.ShowDialog(this)!=true)return;
        await ProjectStore.SaveAsync(dialog.FileName,new {application="Flow Studio",project=projectName,created=DateTime.Now,scenes=shots.Select((a,i)=>new {scene=i+1,a.Name,a.Path,a.Kind,a.Prompt,a.Model})});
        Status("Раскадровка сохранена");
    }

    static string? ProjectNameDialog(string title,string caption,string initial)
    {
        var box=new TextBox{Text=initial,MinWidth=320,Margin=new Thickness(0,12,0,18)};
        var ok=new Button{Content="Создать",Style=(Style)Application.Current.FindResource("AccentButton"),IsDefault=true,MinWidth=100,HorizontalAlignment=HorizontalAlignment.Right};
        var panel=new StackPanel{Margin=new Thickness(25)};panel.Children.Add(new TextBlock{Text=caption,FontSize=16});panel.Children.Add(box);panel.Children.Add(ok);
        var dialog=new Window{Title=title,Content=panel,SizeToContent=SizeToContent.WidthAndHeight,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=Application.Current.MainWindow,ShowInTaskbar=false};
        ok.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(box.Text))dialog.DialogResult=true;};dialog.Loaded+=(_,_)=>{box.Focus();box.SelectAll();};
        return dialog.ShowDialog()==true?box.Text.Trim():null;
    }
    async void Window_Closing(object? sender,CancelEventArgs e)
    {
        if(uiSmokeTest){closing=true;return;}
        if(closing)return;
        e.Cancel=true;
        if(closePending)return;
        closePending=true;IsEnabled=false;
        autosave.Stop();playback.Stop();
        bool projectLock=false;
        try
        {
            await CancelLibraryActionsAsync();
            loginCancellation?.Cancel();
            preparationCancellation?.Cancel();generationCancellation?.Cancel();
            cloudCreationCancellation?.Cancel();exportCancellation?.Cancel();
            if(loginCompletion is {} loginDone)await loginDone.Task;
            if(preparationCompletion is {} preparationDone)await preparationDone.Task;
            cloudCreationCancellation?.Cancel();
            if(generating) {generationCancellation?.Cancel();Status("Завершаем локальное ожидание Flow…");}
            if(generationCompletion is {} genCompletion)await genCompletion.Task;
            if(exporting)
            {
                exportCancellation?.Cancel();Status("Завершаем экспорт перед закрытием…");
            }
            if(exportCompletion is { } completion)await completion.Task;
            await projectOperation.WaitAsync();projectLock=true;
            if(!string.IsNullOrWhiteSpace(projectPath) && !await SaveCurrentAsync())
            {
                Status("Приложение осталось открыто: проект не сохранён. Проверьте доступ к папке или сохраните копию.");
                return;
            }
            libraryCancellation?.Cancel();if(libraryTask!=null)await libraryTask;
            if(pendingUpdateStage!=null)AppUpdateService.StartInstaller(pendingUpdateStage);
            lifetime.Cancel();if(previewTask!=null)await previewTask;
            await serviceBrowser.DisposeAsync();creditTimer.Stop();notifications?.Dispose();VideoPreview.Close();http.Dispose();mediaHttp.Dispose();closing=true;Close();
        }
        catch(Exception ex) { Error(ex); }
        finally
        {
            if(projectLock)projectOperation.Release();
            if(!closing)
            {
                closePending=false;IsEnabled=true;
                autosave.Start();playback.Start();
            }
        }
    }
    async void Window_KeyDown(object sender,KeyEventArgs e)
    {
        if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if(e.Key==Key.S){if(await SaveCurrentAsync())Status("Проект сохранён");e.Handled=true;}
            else if(e.Key==Key.O){OpenProject_Click(sender,e);e.Handled=true;}
            else if(e.Key==Key.Enter){Generate_Click(sender,e);e.Handled=true;}
        }
        if(e.Key==Key.Escape) { if(loginInProgress)loginCancellation?.Cancel();else if(generating)generationCancellation?.Cancel();else if(preparingGeneration)preparationCancellation?.Cancel();else if(exporting)exportCancellation?.Cancel();else if(page=="settings")SwitchPage("studio"); }
    }
    void TitleBar_MouseLeftButtonDown(object sender,MouseButtonEventArgs e) { if(e.ClickCount==2)Maximize_Click(sender,e);else if(e.LeftButton==MouseButtonState.Pressed)DragMove(); }
    void Minimize_Click(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    void Maximize_Click(object sender,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
