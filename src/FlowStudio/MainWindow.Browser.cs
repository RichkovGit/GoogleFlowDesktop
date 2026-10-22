using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FlowStudio.Services;

namespace FlowStudio;

public partial class MainWindow
{
    readonly FlowBrowserRuntimeService serviceBrowser=new();
    ComboBox connectionMethod=null!;
    CheckBox countryCompatibility=null!;
    bool UseServiceBrowser=>connectionMethod?.SelectedIndex==1;
    sealed record BrowserPreferences(int Method=1,bool CountryCompatibility=true);
    void InitializeBrowserUi()
    {
        var body=(StackPanel)((ScrollViewer)SettingsPage.Child).Content;
        var row=new StackPanel{Margin=new Thickness(0,18,0,14)};
        row.Children.Add(new TextBlock{Text="Отправка генераций и сообщений агенту",FontWeight=FontWeights.SemiBold});
        connectionMethod=new ComboBox{ItemsSource=new[]{"Напрямую · нативный запрос Flow","Служебный браузер · штатный интерфейс Flow"},SelectedIndex=1,Margin=new Thickness(0,8,0,8),MinWidth=500};row.Children.Add(connectionMethod);
        countryCompatibility=new CheckBox{Content="Региональная совместимость Flow · только проверка страны",IsChecked=true};row.Children.Add(countryCompatibility);
        row.Children.Add(new TextBlock{Text="Окно открывается на время операции и закрывается после получения ответа. Вход и проверки Google выполняются вручную при необходимости. Прямой способ может быть отклонён Google.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)});
        row.Children.Add(ActionButton("Проверить служебное подключение",async()=>
        {
            if(generating||preparingGeneration||loginInProgress)throw new InvalidOperationException("Дождитесь завершения текущей операции.");
            try{await PrepareServiceBrowserAsync(lifetime.Token);Status("Служебный профиль Google подключён. Платные запросы не отправлялись.");}
            finally{await serviceBrowser.StopAsync();}
        }));body.Children.Insert(3,row);
        var path=Path.Combine(ProjectStore.AppDirectory,"browser-transport.json");
        try{if(File.Exists(path)){var pref=JsonSerializer.Deserialize<BrowserPreferences>(File.ReadAllText(path));if(pref!=null){connectionMethod.SelectedIndex=pref.Method==0?0:1;countryCompatibility.IsChecked=pref.CountryCompatibility;}}}catch(Exception ex)when(ex is IOException or JsonException){}
        async void SaveBrowserPreferences(object? sender,RoutedEventArgs args)
        {try{await ProjectStore.SaveAsync(path,new BrowserPreferences(connectionMethod.SelectedIndex,countryCompatibility.IsChecked==true));}catch(Exception ex){Status("Настройки подключения не сохранены: "+ex.Message);}}
        connectionMethod.SelectionChanged+=SaveBrowserPreferences;countryCompatibility.Click+=SaveBrowserPreferences;
    }
    async Task PrepareServiceBrowserAsync(CancellationToken ct)
    {
        if(FlowCredential!=FlowCookieAuthHandler.SessionCredentialMarker)throw new InvalidOperationException("Служебное подключение использует вход Google в профиле Flow Studio.");
        serviceBrowser.EnableCountryCompatibility=countryCompatibility.IsChecked==true;
        flowAuth.Session=await serviceBrowser.EnsureReadyAsync(ct);
        if(RememberToken.IsChecked==true)await secrets.SaveStringAsync("flow-browser-session",JsonSerializer.Serialize(flowAuth.Session),ct);
    }
    async Task<IReadOnlyList<FlowGeneratedMedia>> GenerateThroughBrowserAsync(FlowGenerationRequest request,string directory,IProgress<string>? progress,CancellationToken ct,bool saveProof=false)
    {
        var model=flowModels.SingleOrDefault(m=>m.ModelKey==request.ModelKey)??throw new InvalidOperationException("Обновите каталог моделей Flow.");
        var service=new FlowCloudLibraryService(http,mediaHttp);
        try
        {
            progress?.Report("Открываем служебный профиль Flow…");await PrepareServiceBrowserAsync(ct);
            var known=(await service.ListMediaAsync(request.ProjectId,FlowCredential,ct)).Select(m=>m.MediaName).ToHashSet(StringComparer.Ordinal);
            progress?.Report("Выставляем параметры и отправляем запрос через интерфейс Flow…");
            await serviceBrowser.SubmitVideoUiAsync(request,model,ct);
            progress?.Report("Запрос отправлен. Ожидаем результат в аккаунте Flow…");
            var end=DateTime.UtcNow.AddMinutes(12);
            while(DateTime.UtcNow<end)
            {
                ct.ThrowIfCancellationRequested();
                await serviceBrowser.ObserveActionAsync(ct);
                var items=await service.ListMediaAsync(request.ProjectId,FlowCredential,ct);
                var matching=items.Where(m=>!known.Contains(m.MediaName)&&m.Kind==request.Kind&&m.Prompt.Trim()==request.Prompt.Trim()).ToArray();
                if(matching.Any(m=>m.Status is "failed" or "canceled"))throw new InvalidOperationException("Google завершил генерацию с ошибкой. Автоматического повтора не было.");
                var ready=matching.Where(m=>m.Status is not("pending" or "scheduled" or "failed" or "canceled")).ToArray();
                if(ready.Length>=request.Count)
                {
                    if(ready.Length!=request.Count)throw new InvalidOperationException("Flow вернул несколько совпадающих генераций. Проверьте медиатеку: повтор не выполнялся.");
                    var result=new List<FlowGeneratedMedia>();
                    foreach(var media in ready){var path=await service.DownloadMediaAsync(media.MediaName,request.ProjectId,media.Kind,FlowCredential,directory,ct);result.Add(new(media.MediaName,path,media.Kind,media.WorkflowId));}
                    return result;
                }
                await Task.Delay(TimeSpan.FromSeconds(4),ct);
            }
            throw new TimeoutException("Ожидание Flow истекло. Генерация может продолжаться в аккаунте; обновите медиатеку перед повтором.");
        }
        finally{if(saveProof)try{await serviceBrowser.SaveProofAsync(directory,CancellationToken.None);}catch{}await serviceBrowser.StopAsync();}
    }
    async Task<FlowAgentConversation> SendAgentThroughBrowserAsync(string projectId,string prompt,FlowAgentConversation? current,IProgress<string>? progress,CancellationToken ct,string? proofDirectory=null)
    {
        using var service=new FlowAgentService(http,flowAuth);
        try
        {
            await PrepareServiceBrowserAsync(ct);
            var before=await service.ListSessionsAsync(projectId,FlowCredential,ct);
            var previousCount=current?.Turns.Count??0;
            if(current!=null&&before.Count(s=>s.Title==current.Title)!=1)throw new InvalidOperationException("Название сеанса неоднозначно в интерфейсе Flow. Выберите другой сеанс или создайте новый.");
            await serviceBrowser.SubmitAgentUiAsync(projectId,prompt,current?.Title,ct);
            if(proofDirectory!=null)await serviceBrowser.SaveProofAsync(proofDirectory,ct);
            progress?.Report("Запрос отправлен агенту штатным интерфейсом Flow…");
            var end=DateTime.UtcNow.AddMinutes(10);var known=before.Select(s=>s.Id).ToHashSet(StringComparer.Ordinal);var extraProof=false;
            while(DateTime.UtcNow<end)
            {
                await Task.Delay(TimeSpan.FromSeconds(2),ct);
                await serviceBrowser.ObserveActionAsync(ct);
                if(proofDirectory!=null&&!extraProof&&DateTime.UtcNow>end.AddMinutes(-10).AddSeconds(10)){await serviceBrowser.SaveProofAsync(proofDirectory,ct);extraProof=true;}
                var sessions=await service.ListSessionsAsync(projectId,FlowCredential,ct);
                var candidates=current==null?sessions.Where(s=>!known.Contains(s.Id)).OrderByDescending(s=>s.UpdatedAt):sessions.Where(s=>s.Id==current.Id).OrderByDescending(s=>s.UpdatedAt);
                foreach(var candidate in candidates.Take(5))
                {
                    var loaded=await service.GetSessionAsync(candidate.Id,FlowCredential,ct);
                    var last=loaded.Turns.LastOrDefault();
                    if(loaded.Turns.Count<=previousCount||last?.UserText.Trim()!=prompt.Trim())continue;
                    progress?.Report(last.AssistantText);
                    if(!await serviceBrowser.IsAgentBusyAsync(ct)&&(!string.IsNullOrWhiteSpace(last.AssistantText)||last.RequiresApproval))return loaded;
                }
            }
            throw new TimeoutException("Агент не завершил ответ за отведённое время. Обновите сеансы перед повтором.");
        }
        finally{if(proofDirectory!=null)try{await serviceBrowser.SaveProofAsync(proofDirectory,CancellationToken.None);}catch{}await serviceBrowser.StopAsync();}
    }
}
