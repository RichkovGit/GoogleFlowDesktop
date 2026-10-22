using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlowStudio.Services;

namespace FlowStudio;
public partial class MainWindow
{
    ComboBox agentProjectBox=null!;
    ListBox agentSessions=null!;
    TextBox agentHistory=null!,agentPrompt=null!;
    TextBlock agentStatus=null!;
    WrapPanel agentApproval=null!;
    WrapPanel agentGallery=null!;
    FlowAgentConversation? conversation;
    bool loadingAgent;

    void BuildAgentPage()
    {
        CreateFeaturePage("agent","Агент Flow","Сеансы и ответы агента вашего аккаунта Google Flow.",out var body);
        agentProjectBox=new ComboBox{ItemsSource=cloudProjects,DisplayMemberPath="Title",MinWidth=300,HorizontalAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(0,0,0,10)};
        agentProjectBox.SelectionChanged+=async(_,_)=>{if(!generating)await LoadAgentSessionsAsync();};body.Children.Add(agentProjectBox);
        var actions=new WrapPanel();actions.Children.Add(ActionButton("Обновить сеансы",LoadAgentSessionsAsync));actions.Children.Add(ActionButton("Новый сеанс",NewAgentSessionAsync,true));actions.Children.Add(ActionButton("Инструкции и настройки",EditAgentSettingsAsync));body.Children.Add(actions);
        agentSessions=new ListBox{DisplayMemberPath="Title",Height=110};
        agentSessions.SelectionChanged+=async(_,_)=>
        {
            if(generating||loadingAgent||agentSessions.SelectedItem is not FlowAgentSessionSummary session)return;
            try{var revision=accountRevision;using var service=new FlowAgentService(http,flowAuth);var loaded=await service.GetSessionAsync(session.Id,FlowCredential,lifetime.Token);if(revision==accountRevision&&Equals(agentSessions.SelectedItem,session)){conversation=loaded;RenderAgentConversation();}}
            catch(Exception ex){agentStatus.Text=ex.Message;}
        };body.Children.Add(agentSessions);
        agentHistory=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=230,MaxHeight=420,Margin=new Thickness(0,10,0,10)};body.Children.Add(agentHistory);
        agentGallery=new WrapPanel{Margin=new Thickness(0,0,0,10)};body.Children.Add(agentGallery);
        agentApproval=new WrapPanel{Visibility=Visibility.Collapsed};
        agentApproval.Children.Add(ActionButton("Разрешить это действие",()=>SendAgentAsync(1),true));agentApproval.Children.Add(ActionButton("Отказать",()=>SendAgentAsync(3)));body.Children.Add(agentApproval);
        agentPrompt=AddField(body,"Сообщение агенту","",true);
        var sendRow=new WrapPanel();sendRow.Children.Add(ActionButton("Отправить агенту",()=>SendAgentAsync(),true));sendRow.Children.Add(ActionButton("Остановить ожидание",()=>{generationCancellation?.Cancel();return Task.CompletedTask;}));body.Children.Add(sendRow);
        agentStatus=new TextBlock{Text="Выберите проект и сеанс. Способ отправки — в разделе «Аккаунт и GPU»: напрямую или служебный браузер. Используются аккаунт и кредиты Flow.",Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,10,0,0)};body.Children.Add(agentStatus);
    }
    async Task LoadAgentSessionsAsync()
    {
        if(loadingAgent||generating||closePending)return;
        if(agentProjectBox.SelectedItem is not CloudProjectCard project){agentStatus.Text="Обновите проекты аккаунта и выберите проект.";return;}
        loadingAgent=true;var revision=accountRevision;
        try
        {
            agentStatus.Text="Получаем историю сеансов Flow…";
            using var service=new FlowAgentService(http,flowAuth);
            var sessions=await service.ListSessionsAsync(project.Id,FlowCredential,lifetime.Token);
            if(revision!=accountRevision)return;
            conversation=null;agentHistory.Clear();agentGallery.Children.Clear();agentSessions.ItemsSource=sessions;agentApproval.Visibility=Visibility.Collapsed;
            agentStatus.Text=$"Сеансов в проекте: {sessions.Count}.";
        }
        catch(Exception ex){agentStatus.Text=ex.Message;}
        finally{loadingAgent=false;}
    }
    async Task NewAgentSessionAsync()
    {
        if(generating||closePending)return;
        if(agentProjectBox.SelectedItem is not CloudProjectCard project)throw new InvalidOperationException("Выберите проект Flow.");
        if(UseServiceBrowser){conversation=null;agentSessions.SelectedItem=null;RenderAgentConversation();agentStatus.Text="Новый сеанс создастся в служебном Flow при отправке сообщения.";return;}
        var revision=accountRevision;
        using var service=new FlowAgentService(http,flowAuth);
        var created=await service.CreateSessionAsync(project.Id,FlowCredential,lifetime.Token);
        if(revision!=accountRevision)return;
        await LoadAgentSessionsAsync();conversation=created;RenderAgentConversation();agentStatus.Text="Новый сеанс Flow создан.";
    }
    void RenderAgentConversation()
    {
        agentGallery.Children.Clear();
        if(conversation==null){agentHistory.Clear();return;}
        agentHistory.Text=string.Join("\n\n",conversation.Turns.Select(t=>"ВЫ\n"+t.UserText+"\n\nАГЕНТ FLOW\n"+FlowAgentPresentation.Parse(t.AssistantText).Text+(t.RequiresApproval?$"\n\nТребуется подтверждение{(t.CreditCost is {} cost?$": {cost} кредитов":"")}":"")));
        foreach(var card in conversation.Turns.TakeLast(3).SelectMany(t=>FlowAgentPresentation.Parse(t.AssistantText).Media).DistinctBy(c=>c.MediaId))
        {
            var panel=new StackPanel{Width=210,Margin=new Thickness(4)};
            panel.Children.Add(new TextBlock{Text=card.Kind=="video"?"▶ ВИДЕО FLOW":"◇ ИЗОБРАЖЕНИЕ FLOW",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)});
            panel.Children.Add(new TextBlock{Text=string.IsNullOrWhiteSpace(card.Caption)?"Результат генерации":card.Caption,TextWrapping=TextWrapping.Wrap,MaxHeight=100});
            var button=ActionButton("Открыть результат",async()=>
            {
                if(generating)throw new InvalidOperationException("Дождитесь ответа агента.");
                var asset=cloudAssets.FirstOrDefault(a=>a.CloudMediaName==card.MediaId);
                if(asset==null){await RefreshCloudLibraryAsync(lifetime.Token);asset=cloudAssets.FirstOrDefault(a=>a.CloudMediaName==card.MediaId);}
                if(asset==null)throw new InvalidOperationException("Результат ещё не появился в медиатеке. Обновите её позже.");
                await EnsureCloudAssetAsync(asset,lifetime.Token);SelectAsset(asset);SwitchPage("studio");
            });panel.Children.Add(button);agentGallery.Children.Add(new Border{Child=panel,BorderBrush=(Brush)FindResource("Muted"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Margin=new Thickness(0,0,10,10),Padding=new Thickness(8)});
        }
        var pending=conversation.Turns.LastOrDefault();agentApproval.Visibility=pending?.RequiresApproval==true&&pending.CanApprove?Visibility.Visible:Visibility.Collapsed;
        if(pending?.RequiresApproval==true&&!pending.CanApprove)agentStatus.Text="Flow запросил подтверждение в новом формате. Его отправка пока не подключена; действие автоматически не разрешается.";
        agentHistory.ScrollToEnd();
    }
    async Task SendAgentAsync(int permissionAction=0)
    {
        if(generating||preparingGeneration||exporting||loadingAgent||closePending)throw new InvalidOperationException("Дождитесь завершения текущей операции.");
        if(agentProjectBox.SelectedItem is not CloudProjectCard project)throw new InvalidOperationException("Выберите проект Flow.");
        if(permissionAction!=0&&conversation?.Turns.LastOrDefault() is not {RequiresApproval:true,CanApprove:true})throw new InvalidOperationException("Нет действия, ожидающего подтверждения.");
        var prompt=permissionAction==0?agentPrompt.Text.Trim():permissionAction==3?"Отклонить действие":"Разрешить это действие";
        if(string.IsNullOrWhiteSpace(prompt))throw new InvalidOperationException("Введите сообщение агенту.");
        if(!await projectOperation.WaitAsync(0))throw new InvalidOperationException("Дождитесь завершения работы с проектом.");
        var revision=accountRevision;var credential=FlowCredential;
        generating=true;generationCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);generationCompletion=completion;
        agentProjectBox.IsEnabled=false;agentSessions.IsEnabled=false;GenerateButton.Content="Остановить агента  ■";
        try
        {
            using var service=new FlowAgentService(http,flowAuth);
            if(!UseServiceBrowser)conversation??=await service.CreateSessionAsync(project.Id,credential,generationCancellation.Token);
            var previous=agentHistory.Text;agentStatus.Text="Агент Flow отвечает…";
            var progress=new Progress<string>(text=>{if(revision==accountRevision){agentHistory.Text=previous+"\n\nВЫ\n"+prompt+"\n\nАГЕНТ FLOW\n"+FlowAgentPresentation.Parse(text).Text;agentHistory.ScrollToEnd();}});
            if(UseServiceBrowser&&permissionAction!=0)throw new InvalidOperationException("Подтверждение этого действия пока нужно выполнить в Flow. Автоматически оно не разрешалось.");
            var updated=UseServiceBrowser?await SendAgentThroughBrowserAsync(project.Id,prompt,conversation,progress,generationCancellation.Token):await service.SendAsync(project.Id,conversation!.Id,prompt,conversation.Turns.Count+1,credential,progress,generationCancellation.Token,permissionAction);
            if(revision!=accountRevision)return;
            conversation=updated;RenderAgentConversation();agentPrompt.Clear();agentStatus.Text="Ответ сохранён в сеансе Flow.";
        }
        catch(OperationCanceledException){agentStatus.Text="Локальное ожидание остановлено. Действие в Google может продолжаться; обновите историю перед повтором.";}
        catch(Exception ex){agentStatus.Text=ex.Message;}
        finally{generating=false;generationCancellation.Dispose();generationCancellation=null;GenerateButton.Content="Создать  ↗";agentProjectBox.IsEnabled=true;agentSessions.IsEnabled=true;projectOperation.Release();completion.TrySetResult(true);}
        if(revision==accountRevision)await RefreshCloudLibraryAsync(lifetime.Token);
    }

    async Task EditAgentSettingsAsync()
    {
        if(generating||closePending)throw new InvalidOperationException("Дождитесь завершения текущей операции.");
        if(agentProjectBox.SelectedItem is not CloudProjectCard project)throw new InvalidOperationException("Выберите проект Flow.");
        var revision=accountRevision;
        using var service=new FlowAgentService(http,flowAuth);
        var current=await service.GetSettingsAsync(project.Id,FlowCredential,lifetime.Token);
        if(revision!=accountRevision)return;
        var content=new StackPanel{Margin=new Thickness(22)};
        content.Children.Add(new TextBlock{Text="Настройки агента · "+project.Title,FontSize=21,FontWeight=FontWeights.SemiBold});
        content.Children.Add(new TextBlock{Text="Подтверждение перед созданием медиа",Margin=new Thickness(0,16,0,8)});
        var policy=new ComboBox{ItemsSource=new[]{"Всегда спрашивать","Никогда · разрешить агенту расходовать кредиты"},SelectedIndex=current.ApprovalPolicy==2?1:0};content.Children.Add(policy);
        content.Children.Add(new TextBlock{Text="Инструкции проекта · одновременно включены до трёх",Margin=new Thickness(0,20,0,8)});
        var cards=new StackPanel();content.Children.Add(cards);
        var editors=new List<(FlowAgentInstruction Original,CheckBox Enabled,TextBox Title,TextBox Description,Border Panel)>();
        void AddInstruction(FlowAgentInstruction instruction)
        {
            var fields=new StackPanel{Margin=new Thickness(14)};
            var enabled=new CheckBox{Content="Включена",IsChecked=instruction.Enabled};fields.Children.Add(enabled);
            var title=AddField(fields,"Название",instruction.Title);var description=AddField(fields,"Инструкция",instruction.Description,true);
            var refs=(instruction.ImageMediaIds?.Count??0)+(instruction.EntityIds?.Count??0)+(instruction.LikenessIds?.Count??0);
            if(refs>0)fields.Children.Add(new TextBlock{Text=$"Связанных референсов: {refs}. Связи сохраняются при редактировании.",Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,8,0,0)});
            var panel=new Border{Background=new SolidColorBrush(Color.FromRgb(29,35,35)),CornerRadius=new CornerRadius(10),Margin=new Thickness(0,0,0,10),Child=fields};
            fields.Children.Add(ActionButton("Убрать инструкцию",()=>{cards.Children.Remove(panel);editors.RemoveAll(e=>e.Panel==panel);return Task.CompletedTask;}));
            editors.Add((instruction,enabled,title,description,panel));cards.Children.Add(panel);
        }
        foreach(var instruction in current.Instructions)AddInstruction(instruction);
        content.Children.Add(ActionButton("＋ Добавить инструкцию",()=>{AddInstruction(new(Guid.NewGuid().ToString(),"","",true));return Task.CompletedTask;}));
        var defaultEditors=new List<(string Kind,ComboBox Model,ComboBox Aspect,ComboBox Count)>();
        void AddDefaults(string kind,FlowAgentDefaults? defaults)
        {
            content.Children.Add(new TextBlock{Text=kind=="video"?"Видео по умолчанию":"Изображения по умолчанию",FontSize=17,Margin=new Thickness(0,20,0,8)});
            var models=FlowModelCatalogService.GetTextModels(flowModels,kind,null,creditBalance?.ModelUserTier);
            var model=new ComboBox{ItemsSource=models,DisplayMemberPath="SelectionLabel"};model.SelectedItem=models.FirstOrDefault(m=>m.ModelKey==defaults?.ModelKey);content.Children.Add(model);
            var aspects=kind=="video"?new[]{"16:9","9:16"}:new[]{"16:9","4:3","1:1","3:4","9:16"};
            var aspect=new ComboBox{ItemsSource=aspects,Margin=new Thickness(0,8,0,0)};
            aspect.SelectedIndex=kind=="video"?defaults?.AspectRatio==1?1:0:defaults?.AspectRatio switch{1=>2,2=>4,4=>3,5=>1,_=>0};content.Children.Add(aspect);
            var count=new ComboBox{ItemsSource=new[]{1,2,3,4},SelectedIndex=Math.Clamp((defaults?.Count??1)-1,0,3),Margin=new Thickness(0,8,0,0)};content.Children.Add(count);defaultEditors.Add((kind,model,aspect,count));
        }
        AddDefaults("image",current.ImageDefaults);AddDefaults("video",current.VideoDefaults);
        var status=new TextBlock{Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,14,0,8)};content.Children.Add(status);
        var dialog=new Window{Title="Агент Flow · настройки",Width=760,Height=780,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)FindResource(typeof(Window)),Content=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        content.Children.Add(ActionButton("Сохранить в аккаунте Flow",async()=>
        {
            if(revision!=accountRevision)throw new InvalidOperationException("Аккаунт изменился. Откройте настройки заново.");
            var instructions=editors.Select(e=>e.Original with{Title=e.Title.Text.Trim(),Description=e.Description.Text,Enabled=e.Enabled.IsChecked==true}).ToArray();
            if(instructions.Count(i=>i.Enabled)>3)throw new InvalidOperationException("Можно включить до трёх инструкций.");
            await service.UpdateInstructionsAsync(project.Id,instructions,FlowCredential,lifetime.Token);
            await service.UpdateApprovalPolicyAsync(project.Id,policy.SelectedIndex==1?2:1,FlowCredential,lifetime.Token);
            foreach(var editor in defaultEditors)
                if(editor.Model.SelectedItem is FlowCatalogModel model)
                {var ratio=(string)editor.Aspect.SelectedItem;var code=editor.Kind=="video"?(ratio=="9:16"?1:2):ratio switch{"1:1"=>1,"9:16"=>2,"16:9"=>3,"3:4"=>4,"4:3"=>5,_=>3};await service.UpdateDefaultsAsync(project.Id,editor.Kind,new(model.ModelKey,code,(int)editor.Count.SelectedItem),FlowCredential,lifetime.Token);}
            status.Text="Настройки сохранены в проекте Flow.";
        },true));
        dialog.ShowDialog();
    }
}
