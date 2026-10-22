using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlowStudio.Services;

public sealed record AppUpdateManifest(string Version,string PackageUrl,string Sha256,long SizeBytes,int UpdaterProtocol=1);
public sealed record AvailableAppUpdate(AppUpdateManifest Manifest,byte[] SignedBytes,byte[] Signature);

/// <summary>Publisher-signed releases from one fixed repository. No Flow credentials are used.</summary>
public sealed class AppUpdateService
{
    public const string Repository="RichkovGit/GoogleFlowDesktop";
    public static Version CurrentVersion=>Assembly.GetExecutingAssembly().GetName().Version??new(0,0);
    readonly HttpClient client;
    public AppUpdateService(HttpClient? http=null)
    {client=http??new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=TimeSpan.FromMinutes(20)};}
    public async Task<AvailableAppUpdate?> CheckAsync(CancellationToken ct=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var release=await ReadAsync(new("https://api.github.com/repos/"+Repository+"/releases/latest"),128*1024,deadline.Token,allowNotFound:true);
        if(release.Length==0)return null;
        using var json=JsonDocument.Parse(release);
        if(json.RootElement.GetProperty("draft").GetBoolean()||json.RootElement.GetProperty("prerelease").GetBoolean())return null;
        var assets=json.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        string? Asset(string name)=>assets.FirstOrDefault(a=>a.GetProperty("name").GetString()==name) is var a&&a.ValueKind==JsonValueKind.Object?a.GetProperty("browser_download_url").GetString():null;
        var address=Asset("FlowStudio-update.json");var signature=Asset("FlowStudio-update.sig");
        if(address==null||signature==null)return null;
        var signed=await ReadAsync(ReleaseUri(address),32*1024,deadline.Token);
        var sig=await ReadAsync(ReleaseUri(signature),2048,deadline.Token);
        var manifest=VerifyManifest(signed,sig);
        if(System.Version.Parse(manifest.Version)<=CurrentVersion)return null;
        if(!assets.Any(a=>a.GetProperty("browser_download_url").GetString()==manifest.PackageUrl))throw new InvalidDataException("Пакет отсутствует среди файлов проверенного релиза.");
        return new(manifest,signed,sig);
    }
    public static AppUpdateManifest VerifyManifest(byte[] bytes,byte[] signature,string? publicPem=null)
    {
        if(bytes.Length is <2 or >32768||signature.Length is <256 or >1024)throw new InvalidDataException("Некорректная подпись обновления.");
        if(publicPem==null)
        {
            using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("FlowStudio.update-public.pem")??throw new InvalidDataException("Ключ обновлений отсутствует.");
            using var reader=new StreamReader(stream);publicPem=reader.ReadToEnd();
        }
        using var rsa=RSA.Create();rsa.ImportFromPem(publicPem);
        if(!rsa.VerifyData(bytes,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new InvalidDataException("Подпись издателя обновления не прошла проверку.");
        var manifest=JsonSerializer.Deserialize<AppUpdateManifest>(bytes)??throw new InvalidDataException("Пустой манифест обновления.");
        if(!System.Version.TryParse(manifest.Version,out var version)||version.Major<1||manifest.UpdaterProtocol!=1||manifest.SizeBytes is <1024 or >800L*1024*1024||manifest.Sha256.Length!=64||!manifest.Sha256.All(char.IsAsciiHexDigit))throw new InvalidDataException("Неподдерживаемые параметры обновления.");
        _=ReleaseUri(manifest.PackageUrl);return manifest;
    }
    static Uri ReleaseUri(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="github.com"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||!uri.AbsolutePath.StartsWith("/"+Repository+"/releases/download/",StringComparison.Ordinal))throw new InvalidDataException("Обновления разрешены только из официального репозитория приложения.");
        return uri;
    }
    async Task<HttpResponseMessage> OpenAsync(Uri uri,CancellationToken ct)
    {
        for(var hop=0;hop<6;hop++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.UserAgent.ParseAdd("FlowStudio/"+CurrentVersion);
            var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if((int)response.StatusCode is not(301 or 302 or 303 or 307 or 308))return response;
            var next=response.Headers.Location;response.Dispose();if(next==null)throw new InvalidDataException("Некорректное перенаправление обновления.");
            next=next.IsAbsoluteUri?next:new Uri(uri,next);
            if(next.Scheme!="https"||!next.IsDefaultPort||next.UserInfo.Length>0||next.Fragment.Length>0||next.Host is not("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))throw new InvalidDataException("Неподдерживаемый сервер загрузки обновления.");
            uri=next;
        }
        throw new InvalidDataException("Слишком много перенаправлений обновления.");
    }
    async Task<byte[]> ReadAsync(Uri uri,int limit,CancellationToken ct,bool allowNotFound=false)
    {
        using var response=await OpenAsync(uri,ct);if(allowNotFound&&response.StatusCode==HttpStatusCode.NotFound)return [];
        response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("Ответ обновления слишком велик.");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var memory=new MemoryStream();var buffer=new byte[16384];int read;
        while((read=await stream.ReadAsync(buffer,ct))>0){if(memory.Length+read>limit)throw new InvalidDataException("Ответ обновления слишком велик.");await memory.WriteAsync(buffer.AsMemory(0,read),ct);}return memory.ToArray();
    }
    public async Task<string> StageAsync(AvailableAppUpdate update,IProgress<string>? progress=null,CancellationToken ct=default)
    {
        var manifest=VerifyManifest(update.SignedBytes,update.Signature);
        if(manifest!=update.Manifest||System.Version.Parse(manifest.Version)<=CurrentVersion)throw new InvalidDataException("Обновление устарело или изменилось.");
        var root=Path.Combine(ProjectStore.AppDirectory,"Updates",manifest.Version+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var package=Path.Combine(root,"package.zip");
        using(var response=await OpenAsync(ReleaseUri(manifest.PackageUrl),ct))
        {
            response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength is {} size&&size!=manifest.SizeBytes)throw new InvalidDataException("Размер пакета обновления изменился.");
            await using var source=await response.Content.ReadAsStreamAsync(ct);await using var target=new FileStream(package,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true);
            var buffer=new byte[65536];long total=0;int read;
            while((read=await source.ReadAsync(buffer,ct))>0){total+=read;if(total>manifest.SizeBytes)throw new InvalidDataException("Пакет больше подписанного размера.");await target.WriteAsync(buffer.AsMemory(0,read),ct);progress?.Report($"Скачано {total/1048576d:0.#} / {manifest.SizeBytes/1048576d:0.#} МБ");}
            if(total!=manifest.SizeBytes)throw new InvalidDataException("Пакет скачан не полностью.");
        }
        await using(var packageStream=File.OpenRead(package))
        {var hash=Convert.ToHexString(await SHA256.HashDataAsync(packageStream,ct));if(!hash.Equals(manifest.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Контрольная сумма обновления не совпала.");}
        var stage=Path.Combine(root,"app");ExtractPackage(package,stage);
        if(!File.Exists(Path.Combine(stage,"FlowStudio.exe")))throw new InvalidDataException("Пакет не содержит приложение.");
        var binaryVersion=FileVersionInfo.GetVersionInfo(Path.Combine(stage,"FlowStudio.exe"));var expected=System.Version.Parse(manifest.Version);
        if(binaryVersion.FileMajorPart!=expected.Major||binaryVersion.FileMinorPart!=expected.Minor||binaryVersion.FileBuildPart!=expected.Build)throw new InvalidDataException("Версия программы не совпала с подписанным манифестом.");
        await File.WriteAllBytesAsync(Path.Combine(root,"manifest.json"),update.SignedBytes,ct);await File.WriteAllBytesAsync(Path.Combine(root,"manifest.sig"),update.Signature,ct);
        return stage;
    }
    internal static void ExtractPackage(string archive,string destination)
    {
        var root=Path.GetFullPath(destination)+Path.DirectorySeparatorChar;Directory.CreateDirectory(root);
        using var zip=ZipFile.OpenRead(archive);long total=0;var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            total+=entry.Length;if(zip.Entries.Count>5000||total>2L*1024*1024*1024)throw new InvalidDataException("Обновление превышает допустимый размер.");
            if(Path.IsPathRooted(entry.FullName)||entry.FullName.Contains(':')||((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("Недопустимый путь в обновлении.");
            var target=Path.GetFullPath(Path.Combine(root,entry.FullName));if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!paths.Add(target))throw new InvalidDataException("Некорректный путь или дубликат в обновлении.");
            if(entry.FullName.EndsWith('/')||entry.FullName.EndsWith('\\')){Directory.CreateDirectory(target);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);entry.ExtractToFile(target,false);
        }
    }
    public static void StartInstaller(string stage)
    {
        var start=new ProcessStartInfo(Path.Combine(stage,"FlowStudio.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(Environment.ProcessId.ToString());start.ArgumentList.Add(AppContext.BaseDirectory);Process.Start(start);
    }
    public static async Task<int> ApplyUpdateAsync(int parentPid,string targetDirectory)
    {
        var source=Path.GetFullPath(AppContext.BaseDirectory);var target=Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(source.Equals(target,StringComparison.OrdinalIgnoreCase)||!File.Exists(Path.Combine(target,"FlowStudio.exe")))return 2;
        var root=Directory.GetParent(source.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var manifest=VerifyManifest(await File.ReadAllBytesAsync(Path.Combine(root,"manifest.json")),await File.ReadAllBytesAsync(Path.Combine(root,"manifest.sig")));
        var zipPath=Path.Combine(root,"package.zip");await using(var zip=File.OpenRead(zipPath))if(!Convert.ToHexString(await SHA256.HashDataAsync(zip)).Equals(manifest.Sha256,StringComparison.OrdinalIgnoreCase))return 3;
        source=Path.Combine(root,"verified-"+Guid.NewGuid().ToString("N"));ExtractPackage(zipPath,source);
        try{using var parent=Process.GetProcessById(parentPid);using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(90));await parent.WaitForExitAsync(wait.Token);}catch(ArgumentException){}catch(OperationCanceledException){return 4;}
        var backup=Path.Combine(root,"backup");var installed=new List<(string Target,string? Backup)>();
        try
        {
            foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
            {
                var relative=Path.GetRelativePath(source,file);var output=Path.GetFullPath(Path.Combine(target,relative));if(!output.StartsWith(target,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Недопустимая папка установки.");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);string? old=null;
                if(File.Exists(output)){old=Path.Combine(backup,relative);Directory.CreateDirectory(Path.GetDirectoryName(old)!);File.Copy(output,old,false);}
                installed.Add((output,old));var temp=output+".update-"+Guid.NewGuid().ToString("N");File.Copy(file,temp,false);File.Move(temp,output,true);
            }
            Process.Start(new ProcessStartInfo(Path.Combine(target,"FlowStudio.exe")){UseShellExecute=true,WorkingDirectory=target});return 0;
        }
        catch(Exception error)
        {
            foreach(var item in installed.AsEnumerable().Reverse())try{if(item.Backup!=null)File.Copy(item.Backup,item.Target,true);else File.Delete(item.Target);}catch{}
            await File.WriteAllTextAsync(Path.Combine(root,"install-error.txt"),error.GetType().Name+": "+error.Message);
            Process.Start(new ProcessStartInfo(Path.Combine(target,"FlowStudio.exe")){UseShellExecute=true,WorkingDirectory=target});return 1;
        }
    }
}
