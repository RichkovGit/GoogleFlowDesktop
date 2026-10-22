using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlowStudio.Services;
using Microsoft.Win32;

namespace FlowStudio;
public partial class MainWindow
{
    ComboBox generationCountBox=null!;
    readonly Dictionary<string,FlowImageReference> uploadedReferences=[];
    void InitializeReferenceUi()
    {
        var panel=(StackPanel)ModeBox.Parent;var index=panel.Children.IndexOf(ModeBox)+1;
        panel.Children.Insert(index++,new TextBlock{Text="КОЛИЧЕСТВО РЕЗУЛЬТАТОВ",FontSize=9,Foreground=(System.Windows.Media.Brush)FindResource("Muted"),Margin=new Thickness(0,14,0,7)});
        generationCountBox=new ComboBox{ItemsSource=new[]{1,2,3,4},SelectedIndex=0};generationCountBox.SelectionChanged+=(_,_)=>{if(DurationBox.SelectedItem is FlowCatalogModel m)ApplySelectedModel(m);MarkDirty();};panel.Children.Insert(index,generationCountBox);
    }
    IReadOnlyCollection<int> GenerationInputs=>InputMode switch
    {"frames"=>lastFrame==null?new[]{1,5}:new[]{1,5,7},"ingredients"=>new[]{1,6},"image" when references.Count>0=>new[]{1,3},_=>new[]{1}};
    async void AddReference_Click(object sender,RoutedEventArgs e)
    {
        if(generating||preparingGeneration||exporting||closePending)return;
        var dialog=new OpenFileDialog{Title=InputMode=="frames"?"Первый кадр":"Ингредиенты для Flow",Filter="Изображения|*.png;*.jpg;*.jpeg;*.webp",Multiselect=InputMode!="frames"};if(dialog.ShowDialog(this)!=true)return;
        if(InputMode=="video")ModeBox.SelectedIndex=3;
        if(!await projectOperation.WaitAsync(0))return;
        try
        {
            if(InputMode=="frames")references.Clear();
            foreach(var file in dialog.FileNames.Take(InputMode=="frames"?1:Math.Max(0,3-references.Count)))references.Add(await ProjectStore.ImportAsync(file,projectPath));
            UpdateReferences();RefreshComposerModels();MarkDirty();
        }
        catch(Exception ex){Error(ex);}
        finally{projectOperation.Release();}
    }
    async void LastFrame_Click(object sender,RoutedEventArgs e)
    {
        if(generating||preparingGeneration||exporting||closePending)return;
        if(InputMode!="frames"){Status("Последний кадр доступен в режиме «Кадры → видео»");return;}
        var dialog=new OpenFileDialog{Title="Последний кадр",Filter="Изображения|*.png;*.jpg;*.jpeg;*.webp"};if(dialog.ShowDialog(this)!=true)return;
        if(!await projectOperation.WaitAsync(0))return;
        try{lastFrame=await ProjectStore.ImportAsync(dialog.FileName,projectPath);UpdateReferences();RefreshComposerModels();MarkDirty();}
        catch(Exception ex){Error(ex);}finally{projectOperation.Release();}
    }
    void ClearReferences_Click(object sender,RoutedEventArgs e)
    {if(generating||preparingGeneration)return;references.Clear();lastFrame=null;UpdateReferences();RefreshComposerModels();MarkDirty();}
    async Task<FlowImageReference> UploadReferenceAsync(string file,string projectId,string credential,CancellationToken ct)
    {
        if(!File.Exists(file))throw new FileNotFoundException("Не найдено изображение референса.",file);
        var key=accountRevision+"|"+projectId+"|"+Path.GetFullPath(file)+"|"+File.GetLastWriteTimeUtc(file).Ticks;
        if(uploadedReferences.TryGetValue(key,out var saved))return saved;
        Status("Загружаем изображение в Flow: "+Path.GetFileName(file));
        var uploaded=await new FlowGenerationService(http,mediaHttp).UploadImageAsync(projectId,file,credential,null,ct);
        var reference=uploaded.AsReference();uploadedReferences[key]=reference;return reference;
    }
    async Task<FlowGenerationRequest> AttachReferencesAsync(FlowGenerationRequest request,CancellationToken ct)
    {
        var input=InputMode;var paths=references.ToArray();var end=lastFrame;
        if(input=="frames")
        {
            if(paths.Length==0)throw new InvalidOperationException("Добавьте первый кадр для режима «Кадры → видео».");
            var first=await UploadReferenceAsync(paths[0],request.ProjectId,FlowCredential,ct);
            var last=end==null?null:await UploadReferenceAsync(end,request.ProjectId,FlowCredential,ct);
            return request with{FirstFrame=first,LastFrame=last,OutputResolution=last==null?request.OutputResolution:null};
        }
        if(input=="ingredients"&&paths.Length==0)throw new InvalidOperationException("Добавьте хотя бы один ингредиент.");
        if((input=="ingredients"||request.Kind=="image")&&paths.Length>0)
        {
            var images=new List<FlowImageReference>();foreach(var path in paths)images.Add(await UploadReferenceAsync(path,request.ProjectId,FlowCredential,ct));
            return request with{Images=images};
        }
        return request;
    }
}
