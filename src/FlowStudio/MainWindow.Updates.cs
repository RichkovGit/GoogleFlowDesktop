using System.Windows;
using System.Windows.Controls;
using FlowStudio.Services;

namespace FlowStudio;
public partial class MainWindow
{
    readonly AppUpdateService appUpdates=new();
    AvailableAppUpdate? availableUpdate;
    TextBlock updateStatus=null!;
    Button installUpdateButton=null!;
    string? pendingUpdateStage;
    bool checkingUpdates;
    void InitializeUpdatesUi()
    {
        var body=(StackPanel)((ScrollViewer)SettingsPage.Child).Content;
        body.Children.Add(new Separator{Margin=new Thickness(0,25,0,20)});
        body.Children.Add(new TextBlock{Text="Обновления Flow Studio",FontSize=21,FontWeight=FontWeights.SemiBold});
        updateStatus=new TextBlock{Text="Версия "+AppUpdateService.CurrentVersion.ToString(3)+" · подписанные пакеты GitHub",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,10)};body.Children.Add(updateStatus);
        var row=new WrapPanel();row.Children.Add(ActionButton("Проверить обновления",CheckAppUpdatesAsync));
        installUpdateButton=ActionButton("Скачать и установить",InstallAppUpdateAsync,true);installUpdateButton.IsEnabled=false;row.Children.Add(installUpdateButton);body.Children.Add(row);
        body.Children.Add(new TextBlock{Text="Пакеты проверяются подписью издателя и SHA-256. Проекты и вход хранятся отдельно. Приложение перезапустится после сохранения проекта; предыдущие файлы сохраняются для отката.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,16)});
    }
    async Task CheckAppUpdatesAsync()
    {
        if(checkingUpdates||closePending)return;checkingUpdates=true;
        try{updateStatus.Text="Проверяем подписанный релиз GitHub…";availableUpdate=await appUpdates.CheckAsync(lifetime.Token);installUpdateButton.IsEnabled=availableUpdate!=null;updateStatus.Text=availableUpdate==null?"Версия "+AppUpdateService.CurrentVersion.ToString(3)+" · новых подписанных обновлений нет.":"Доступна версия "+availableUpdate.Manifest.Version+" · "+(availableUpdate.Manifest.SizeBytes/1048576d).ToString("0.#")+" МБ";}
        catch(OperationCanceledException){}
        catch(Exception ex){updateStatus.Text="Не удалось проверить обновления: "+ex.Message;}
        finally{checkingUpdates=false;}
    }
    async Task InstallAppUpdateAsync()
    {
        if(availableUpdate==null)return;
        if(generating||preparingGeneration||exporting||loginInProgress||closePending)throw new InvalidOperationException("Завершите текущие операции перед обновлением.");
        var stage=await appUpdates.StageAsync(availableUpdate,new Progress<string>(s=>updateStatus.Text=s),lifetime.Token);
        if(!await SaveCurrentAsync())return;
        pendingUpdateStage=stage;Close();
    }
}
