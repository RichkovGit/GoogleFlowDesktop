using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlowStudio.Services;

namespace FlowStudio;

public partial class MainWindow
{
    readonly ObservableCollection<Asset> cloudAssets=[];
    readonly ObservableCollection<CloudProjectCard> cloudProjects=[];
    readonly Dictionary<string,FlowCloudMediaInfo> cloudMedia=[];
    readonly Dictionary<string,Button> extraNavigation=[];
    readonly Dictionary<string,Border> extraPages=[];
    readonly SemaphoreSlim cloudSyncGate=new(1,1);
    readonly SemaphoreSlim mediaFetchGate=new(1,1);
    CancellationTokenSource? libraryCancellation;
    Task? libraryTask;
    Task? previewTask;
    ComboBox libraryScope=null!;
    ListBox projectList=null!;
    TextBlock cloudSyncText=null!;
    TextBlock projectCountText=null!;
    int selectionRevision;

    void InitializeExtraUi()
    {
        DurationBox.Items.Clear();
        RememberToken.Click+=async(_,_)=>
        {
            if(RememberToken.IsChecked!=true){secrets.Delete("flow-browser-session");secrets.Delete("flow-oauth");Status("Вход останется в памяти до закрытия приложения");}
            else if(flowAuth.Session!=null)await secrets.SaveStringAsync("flow-browser-session",System.Text.Json.JsonSerializer.Serialize(flowAuth.Session),lifetime.Token);
        };
        var nav=(StackPanel)StudioNav.Parent;
        AddNavigation(nav,nav.Children.IndexOf(QueueNav)+1,"agent","✧     Агент Flow");
        AddNavigation(nav,nav.Children.IndexOf(QueueNav)+1,"editor","▤     Монтаж");
        var projectHeading=nav.Children.OfType<TextBlock>().First(t=>t.Text=="ПРОЕКТЫ");
        AddNavigation(nav,nav.Children.IndexOf(projectHeading)+1,"projects","☁     Проекты Flow");
        var lower=(StackPanel)SettingsNav.Parent;
        AddNavigation(lower,lower.Children.IndexOf(SettingsNav),"automation","↻     Автоматизация");

        var libraryGrid=(Grid)LibraryPage.Child;
        libraryGrid.RowDefinitions.Insert(0,new RowDefinition{Height=GridLength.Auto});
        foreach(UIElement element in libraryGrid.Children)Grid.SetRow(element,Grid.GetRow(element)+1);
        var bar=new DockPanel{Margin=new Thickness(0,0,0,10)};
        var sync=ActionButton("↻ Обновить",async()=>await RunCloudSyncAsync());DockPanel.SetDock(sync,Dock.Right);bar.Children.Add(sync);
        libraryScope=new ComboBox{MinWidth=190,Margin=new Thickness(0,0,8,0)};
        libraryScope.Items.Add("Аккаунт Flow");libraryScope.Items.Add("Текущий проект");libraryScope.SelectedIndex=0;
        libraryScope.SelectionChanged+=(_,_)=>RefreshLibraryScope();bar.Children.Add(libraryScope);
        libraryGrid.Children.Add(bar);
        BuildProjectsPage();BuildEditorPage();BuildAutomationPage();BuildAgentPage();
        RefreshLibraryScope();
    }
    void AddNavigation(StackPanel host,int index,string key,string caption)
    {
        var button=new Button{Content=caption,Style=(Style)FindResource("NavButton")};
        button.Click+=(_,_)=>SwitchPage(key);host.Children.Insert(index,button);extraNavigation[key]=button;
    }
    Border CreateFeaturePage(string key,string title,string subtitle,out StackPanel body)
    {
        body=new StackPanel{Margin=new Thickness(26,24,26,30)};
        body.Children.Add(new TextBlock{Text=title,FontSize=25,FontWeight=FontWeights.SemiBold});
        body.Children.Add(new TextBlock{Text=subtitle,Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,8,0,20)});
        var pageBorder=new Border{Background=(Brush)FindResource("Bg"),Visibility=Visibility.Collapsed,Child=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        Grid.SetRowSpan(pageBorder,3);WorkArea.Children.Add(pageBorder);extraPages[key]=pageBorder;return pageBorder;
    }
    Button ActionButton(string caption,Func<Task> action,bool accent=false)
    {
        var button=new Button{Content=caption,Margin=new Thickness(0,0,8,6),HorizontalAlignment=HorizontalAlignment.Left};
        if(accent)button.Style=(Style)FindResource("AccentButton");
        button.Click+=async(_,_)=>{if(closePending)return;button.IsEnabled=false;try{await action();}catch(OperationCanceledException){}catch(Exception ex){Error(ex);}finally{button.IsEnabled=true;}};
        return button;
    }
    void BuildProjectsPage()
    {
        CreateFeaturePage("projects","Проекты Flow","Проекты вашего аккаунта. Изменения монтажа и черновики сохраняются на этом компьютере.",out var body);
        var actions=new WrapPanel();actions.Children.Add(ActionButton("↻ Обновить аккаунт",RunCloudSyncAsync,true));actions.Children.Add(ActionButton("Открыть с диска",()=>{OpenProject_Click(this,new RoutedEventArgs());return Task.CompletedTask;}));body.Children.Add(actions);
        projectCountText=new TextBlock{Text="Подключите Google, чтобы загрузить проекты.",Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,10,0,12)};body.Children.Add(projectCountText);
        projectList=new ListBox{ItemsSource=cloudProjects,MinHeight=260,MaxHeight=520};
        var template=new DataTemplate(typeof(CloudProjectCard));
        var stack=new FrameworkElementFactory(typeof(StackPanel));
        var title=new FrameworkElementFactory(typeof(TextBlock));title.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("Title"));title.SetValue(TextBlock.FontSizeProperty,16d);title.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);stack.AppendChild(title);
        var detail=new FrameworkElementFactory(typeof(TextBlock));detail.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("Detail"));detail.SetValue(TextBlock.ForegroundProperty,FindResource("Muted"));detail.SetValue(TextBlock.MarginProperty,new Thickness(0,6,0,3));stack.AppendChild(detail);template.VisualTree=stack;projectList.ItemTemplate=template;
        projectList.MouseDoubleClick+=async(_,_)=>{if(projectList.SelectedItem is CloudProjectCard p)try{await OpenCloudProjectAsync(p);}catch(Exception ex){Error(ex);}};
        body.Children.Add(projectList);
        body.Children.Add(ActionButton("Открыть выбранный проект",async()=>{if(projectList.SelectedItem is CloudProjectCard p)await OpenCloudProjectAsync(p);},true));
        cloudSyncText=new TextBlock{Text="",Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,12,0,0)};body.Children.Add(cloudSyncText);
    }
    async Task RunCloudSyncAsync()
    {
        if(!HasFlowCredential){SwitchPage("settings");Status("Сначала подключите Google");return;}
        await RefreshCloudLibraryAsync(lifetime.Token);
    }
    async Task RefreshCloudLibraryAsync(CancellationToken ct)
    {
        if(!HasFlowCredential||closePending||generating||exporting||!await cloudSyncGate.WaitAsync(0,ct))return;
        var revision=accountRevision;var credential=FlowCredential;
        libraryCancellation=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        var token=libraryCancellation.Token;
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);libraryTask=completion.Task;
        try
        {
            cloudSyncText.Text="Получаем проекты аккаунта…";Status(cloudSyncText.Text);
            var service=new FlowCloudLibraryService(http,mediaHttp);
            var projects=await service.ListProjectsAsync(credential,token);
            if(revision!=accountRevision)throw new OperationCanceledException();
            var fresh=new List<(FlowCloudProjectInfo Project,FlowCloudMediaInfo Media)>();
            var selectedAgentProject=(agentProjectBox.SelectedItem as CloudProjectCard)?.Id;
            var freshProjects=new List<CloudProjectCard>();
            for(var i=0;i<projects.Count;i++)
            {
                token.ThrowIfCancellationRequested();
                cloudSyncText.Text=$"Загружаем содержимое проектов: {i+1} / {projects.Count}";Status(cloudSyncText.Text);
                var items=await service.ListMediaAsync(projects[i].Id,credential,token);
                if(revision!=accountRevision)throw new OperationCanceledException();
                fresh.AddRange(items.Select(m=>(projects[i],m)));
                freshProjects.Add(new(){Id=projects[i].Id,Title=projects[i].Title,Updated=projects[i].UpdatedAt?.LocalDateTime??DateTime.MinValue,Detail=$"{items.Count} {Plural(items.Count,"материал","материала","материалов")}"+(projects[i].UpdatedAt is {} date?$"  ·  {date.LocalDateTime:dd.MM.yyyy HH:mm}":"")});
            }
            cloudProjects.Clear();foreach(var project in freshProjects)cloudProjects.Add(project);
            if(selectedAgentProject!=null)agentProjectBox.SelectedItem=cloudProjects.FirstOrDefault(p=>p.Id==selectedAgentProject);
            cloudAssets.Clear();cloudMedia.Clear();
            foreach(var (project,media) in fresh.OrderByDescending(p=>p.Media.CreatedAt))
            {
                var asset=CreateCloudAsset(project,media);cloudAssets.Add(asset);cloudMedia[asset.Id]=media;
            }
            MergeCurrentCloudAssets();
            projectCountText.Text=$"{projects.Count} {Plural(projects.Count,"проект","проекта","проектов")} · {cloudAssets.Count} материалов в аккаунте";
            cloudSyncText.Text=$"Обновлено {DateTime.Now:HH:mm}. Оригиналы скачиваются при открытии материала.";
            RefreshLibraryScope();Status("Проекты и медиатека аккаунта обновлены");
            await DownloadNewRemoteVideosAsync(token);
            foreach(var asset in cloudAssets.Take(24).ToArray())
            {
                token.ThrowIfCancellationRequested();if(revision!=accountRevision)throw new OperationCanceledException();
                if(asset.Thumbnail!=null||!cloudMedia.TryGetValue(asset.Id,out var media))continue;
                try{var preview=await service.DownloadThumbnailAsync(media,credential,Path.Combine(FlowCloudCache.GetMediaDirectory(asset.CloudProjectId),"previews"),token);if(revision==accountRevision)asset.Thumbnail=ProjectStore.Thumbnail(preview,360);}
                catch(Exception ex) when(ex is FlowCloudLibraryException or System.Net.Http.HttpRequestException or IOException) { }
            }
        }
        catch(OperationCanceledException){if(!closePending)cloudSyncText.Text="Обновление медиатеки остановлено";}
        catch(Exception ex){cloudSyncText.Text="Медиатека не обновлена: "+ex.Message;Status(cloudSyncText.Text);}
        finally{libraryCancellation.Dispose();libraryCancellation=null;cloudSyncGate.Release();completion.TrySetResult();}
    }
    Asset CreateCloudAsset(FlowCloudProjectInfo project,FlowCloudMediaInfo media)
    {
        var asset=new Asset{Id=FlowCloudCache.GetAssetId(project.Id,media.MediaName),Name=media.Title,Kind=media.Kind,Prompt=media.Prompt,Model=media.ModelKey,Created=media.CreatedAt?.LocalDateTime??DateTime.Now,IsGenerated=true,CloudMediaName=media.MediaName,CloudProjectId=project.Id,CloudProjectTitle=project.Title,CloudWorkflowId=media.WorkflowId,CloudCollectionId=media.CollectionId,IsFavorite=media.IsFavorite,IsTrashed=media.IsTrashed};
        var directory=FlowCloudCache.GetMediaDirectory(project.Id);
        var stem=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(media.MediaName))).ToLowerInvariant();
        if(Directory.Exists(directory))asset.Path=Directory.EnumerateFiles(directory,stem+".*").FirstOrDefault(ProjectStore.IsMedia)??"";
        asset.Thumbnail=ProjectStore.Thumbnail(asset.Path,360);
        if(asset.Thumbnail is null&&Directory.Exists(Path.Combine(directory,"previews")))
        {
            var previewStem=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(media.MediaName+"-preview"))).ToLowerInvariant();
            var cachedPreview=Directory.EnumerateFiles(Path.Combine(directory,"previews"),previewStem+".*").FirstOrDefault(ProjectStore.IsMedia);
            if(cachedPreview!=null)asset.Thumbnail=ProjectStore.Thumbnail(cachedPreview,360);
        }
        asset.Availability=media.Status switch{"pending" or "scheduled"=>"В процессе в Flow","failed"=>"Ошибка генерации в Flow","canceled"=>"Отменено",_=>File.Exists(asset.Path)?"На устройстве":"В облаке"};
        return asset;
    }
    void MergeCurrentCloudAssets()
    {
        var id=CloudProjectBox.Text.Trim().Replace("projects/","");
        if(!cloudProjects.Any(p=>p.Id==id))return;
        foreach(var asset in cloudAssets.Where(a=>a.CloudProjectId==id))
            if(!assets.Any(a=>a.CloudMediaName==asset.CloudMediaName))assets.Add(asset);
        MarkDirty();
    }
    void RefreshLibraryScope()
    {
        if(libraryScope==null||LibraryList==null)return;
        LibraryList.ItemsSource=libraryScope.SelectedIndex==0?cloudAssets:assets;
        LibrarySearch_TextChanged(this,new TextChangedEventArgs(TextBox.TextChangedEvent,UndoAction.None));UpdateCounts();
    }
    async Task OpenCloudProjectAsync(CloudProjectCard project)
    {
        if(closePending||generating||preparingGeneration||exporting){Status("Дождитесь завершения текущей задачи");return;}
        if(!cloudProjects.Any(p=>p.Id==project.Id))throw new InvalidOperationException("Обновите список проектов активного аккаунта.");
        if(!await projectOperation.WaitAsync(0))return;
        try
        {
            if(!await SaveCurrentAsync())return;
            var path=FlowCloudCache.GetProjectPath(project.Id);
            if(!File.Exists(path))await ProjectStore.SaveAsync(path,new StudioProject{Name=project.Title,CloudProjectId=project.Id});
            await LoadProjectAsync(path);MergeCurrentCloudAssets();await SaveCurrentAsync();libraryScope.SelectedIndex=1;RefreshLibraryScope();SwitchPage("library");
        }
        finally{projectOperation.Release();}
    }
    async Task EnsureCloudAssetAsync(Asset asset,CancellationToken ct)
    {
        if(File.Exists(asset.Path)||string.IsNullOrEmpty(asset.CloudMediaName))return;
        if(!HasFlowCredential||!cloudMedia.ContainsKey(asset.Id))throw new InvalidOperationException("Обновите медиатеку активного аккаунта перед скачиванием.");
        var revision=accountRevision;var credential=FlowCredential;
        await mediaFetchGate.WaitAsync(ct);
        try
        {
            if(revision!=accountRevision||!cloudMedia.ContainsKey(asset.Id))throw new OperationCanceledException();
            if(File.Exists(asset.Path))return;
            asset.Availability="Скачиваем…";
            var file=await new FlowCloudLibraryService(http,mediaHttp).DownloadMediaAsync(asset.CloudMediaName,asset.CloudProjectId,asset.Kind,credential,FlowCloudCache.GetMediaDirectory(asset.CloudProjectId),ct);
            if(revision!=accountRevision)throw new OperationCanceledException();
            asset.Path=file;asset.Thumbnail=ProjectStore.Thumbnail(file,360);asset.Availability="На устройстве";
            foreach(var other in assets.Concat(cloudAssets).Where(a=>a.CloudMediaName==asset.CloudMediaName)){other.Path=file;other.Thumbnail=asset.Thumbnail;other.Availability=asset.Availability;}
            if(assets.Contains(asset)||assets.Any(a=>a.CloudMediaName==asset.CloudMediaName))MarkDirty();
        }
        catch{asset.Availability="Не удалось скачать";throw;}
        finally{mediaFetchGate.Release();}
    }
    async Task LoadCloudPreviewAsync(Asset asset,int revision)
    {
        try{await EnsureCloudAssetAsync(asset,lifetime.Token);if(revision==selectionRevision&&selectedAsset==asset)SelectAsset(asset);}
        catch(OperationCanceledException){}
        catch(Exception ex){if(selectedAsset==asset)PreviewInfo.Text=ex.Message;Status(ex.Message);}
    }
    void SetExtraPage(string key)
    {
        foreach(var pair in extraPages)pair.Value.Visibility=pair.Key==key?Visibility.Visible:Visibility.Collapsed;
        foreach(var pair in extraNavigation){pair.Value.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Key==key?"#2B3325":"#0015181A"));pair.Value.Foreground=(Brush)FindResource(pair.Key==key?"Accent":"Muted");}
    }
}
