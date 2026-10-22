using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace FlowStudio.Services;
public class ProjectStore
{
    public static string AppDirectory => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowStudio");
    public static string ProjectsDirectory => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flow Studio");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented=true };
    public static async Task SaveAsync<T>(string path,T value)
    {
        var destination=System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // Serialize before creating a file. Failed serialization never changes the old project.
        var json=JsonSerializer.Serialize(value,JsonOptions);
        try
        {
            await File.WriteAllTextAsync(temporary,json);
            for(var attempt=0;;attempt++)
            {
                try{File.Move(temporary,destination,true);break;}
                catch(Exception ex)when(attempt<6&&ex is IOException or UnauthorizedAccessException){await Task.Delay(40*(attempt+1));}
            }
        }
        finally { TryDelete(temporary); }
    }
    public static async Task<T?> ReadAsync<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path),JsonOptions) : default;
    public static string CreateProject(string name)
    {
        var path = System.IO.Path.Combine(ProjectsDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(System.IO.Path.Combine(path,"media"));
        return System.IO.Path.Combine(path,"project.flowstudio");
    }
    public static string MediaDirectory(string projectPath) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(projectPath)!,"media");
    public static bool IsVideo(string path) => new[]{".mp4",".mov",".mkv",".webm",".avi"}.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
    public static bool IsMedia(string path) => IsVideo(path) || new[]{".png",".jpg",".jpeg",".webp",".bmp"}.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
    public static BitmapSource? Thumbnail(string path,int decodePixelWidth=640)
    {
        if(IsVideo(path) || !File.Exists(path)) return null;
        try { var b=new BitmapImage(); b.BeginInit(); b.CacheOption=BitmapCacheOption.OnLoad; b.DecodePixelWidth=decodePixelWidth; b.UriSource=new Uri(System.IO.Path.GetFullPath(path)); b.EndInit(); b.Freeze(); return b; } catch { return null; }
    }
    public static async Task<string> ImportAsync(string source,string projectPath)
    {
        if(!IsMedia(source)) throw new InvalidOperationException("Поддерживаются PNG, JPG, WebP, BMP, MP4, MOV, MKV, WebM и AVI.");
        var directory=System.IO.Path.GetFullPath(MediaDirectory(projectPath));
        string destination = System.IO.Path.Combine(directory,Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(source));
        string temporary=destination+".tmp";
        Directory.CreateDirectory(directory);
        try
        {
            await using(var input=File.OpenRead(source))
            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
            {
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }
            File.Move(temporary,destination,false);
            return destination;
        }
        finally { TryDelete(temporary); }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
    }
}
