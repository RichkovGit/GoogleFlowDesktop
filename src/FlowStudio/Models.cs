using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FlowStudio;
public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name=null) { field=value; Changed(name); }
}
public class Asset : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "image";
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public bool IsGenerated { get; set; }
    public string CloudMediaName { get; set; } = "";
    public string CloudProjectId { get; set; } = "";
    public string CloudProjectTitle { get; set; } = "";
    public string CloudWorkflowId { get; set; } = "";
    public string CloudCollectionId { get; set; } = "";
    public bool IsFavorite { get; set; }
    public bool IsTrashed { get; set; }
    public double TrimStartSeconds { get; set; }
    public double? TrimEndSeconds { get; set; }
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    string availability = "";
    [JsonIgnore] public string Availability { get=>availability; set=>Set(ref availability,value); }
    [JsonIgnore] public string SourceCaption => string.IsNullOrEmpty(CloudProjectTitle) ? "На устройстве" : CloudProjectTitle;
    [JsonIgnore] public string Caption => Kind == "video" ? "ВИДЕО" : "ИЗОБРАЖЕНИЕ";
    BitmapSource? thumbnail;
    [JsonIgnore] public BitmapSource? Thumbnail { get=>thumbnail; set=>Set(ref thumbnail,value); }
    [JsonIgnore] public string Glyph => Kind == "video" ? "▶" : "◇";
}
public sealed class CloudProjectCard
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime Updated { get; init; }
    public string Detail { get; init; } = "";
    public override string ToString() => Title;
}
public class Job : Observable
{
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "";
    string status = "В очереди";
    public string Status { get=>status; set=>Set(ref status,value); }
    public DateTime Created { get; set; } = DateTime.Now;
    [JsonIgnore] public CancellationTokenSource Cancellation { get; } = new();
}
public class StudioProject
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Новый проект";
    public string Draft { get; set; } = "";
    public string Mode { get; set; } = "video";
    public string AspectRatio { get; set; } = "16:9";
    public string Seed { get; set; } = "";
    public string CloudProjectId { get; set; } = "";
    public string FlowModelKey { get; set; } = "";
    public int VariantCount { get; set; } = 1;
    public List<string> ReferencePaths { get; set; } = [];
    public string? LastFramePath { get; set; }
    public DateTime Updated { get; set; } = DateTime.Now;
    public List<Asset> Assets { get; set; } = [];
    public List<string> Storyboard { get; set; } = [];
    public List<ClipEdit> StoryboardEdits { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];
}
public sealed record ClipEdit(double TrimStartSeconds=0,double? TrimEndSeconds=null,double Volume=1,bool Muted=false);
public class StudioSettings
{
    public string LastProject { get; set; } = "";
    public bool PreferDiscrete { get; set; } = true;
}
