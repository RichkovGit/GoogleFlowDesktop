using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlowStudio.Services;
public static class AppUpdateSelfTest
{
    public static async Task<string> RunAsync(string qa,CancellationToken ct=default)
    {
        using var rsa=RSA.Create(2048);var publicKey=rsa.ExportSubjectPublicKeyInfoPem();
        var manifest=new AppUpdateManifest("99.0.0","https://github.com/RichkovGit/GoogleFlowDesktop/releases/download/v99.0.0/FlowStudio.zip",new string('A',64),2048);
        byte[] Bytes(AppUpdateManifest value)=>JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] Sign(byte[] value)=>rsa.SignData(value,HashAlgorithmName.SHA256,RSASignaturePadding.Pss);
        var bytes=Bytes(manifest);var signed=Sign(bytes);
        if(AppUpdateService.VerifyManifest(bytes,signed,publicKey)!=manifest)throw new InvalidOperationException("Signed manifest roundtrip failed.");
        void Reject(byte[] value,byte[] signature)
        {try{AppUpdateService.VerifyManifest(value,signature,publicKey);}catch(Exception ex)when(ex is InvalidDataException or CryptographicException){return;}throw new InvalidOperationException("Unsafe update manifest accepted.");}
        Reject(Bytes(manifest with{Version="99.1.0"}),signed);
        foreach(var invalid in new[]{manifest with{PackageUrl="https://github.com/attacker/project/releases/download/v1/app.zip"},manifest with{PackageUrl="http://github.com/RichkovGit/GoogleFlowDesktop/releases/download/v1/app.zip"},manifest with{Version="../../bad"},manifest with{SizeBytes=long.MaxValue},manifest with{UpdaterProtocol=99},manifest with{Sha256="BAD"}}){var data=Bytes(invalid);Reject(data,Sign(data));}
        using(var wrong=RSA.Create(2048))Reject(bytes,wrong.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        var folder=Path.Combine(qa,"updates");Directory.CreateDirectory(folder);
        void Zip(string path,params string[] names){using var zip=ZipFile.Open(path,ZipArchiveMode.Create);foreach(var name in names){using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write("fixture");}}
        var archive=Path.Combine(folder,"safe.zip");Zip(archive,"FlowStudio.exe","tools/file.txt");AppUpdateService.ExtractPackage(archive,Path.Combine(folder,"safe"));
        if(!File.Exists(Path.Combine(folder,"safe","tools","file.txt")))throw new InvalidOperationException("Valid archive failed.");
        foreach(var (name,entries) in new[]{("traversal",new[]{"../escaped.txt"}),("absolute",new[]{"C:/escaped.txt"}),("duplicate",new[]{"file.txt","FILE.txt"})})
        {var path=Path.Combine(folder,name+".zip");Zip(path,entries);var failed=false;try{AppUpdateService.ExtractPackage(path,Path.Combine(folder,name));}catch(InvalidDataException){failed=true;}if(!failed)throw new InvalidOperationException("Unsafe update archive accepted: "+name);}
        using var http=new HttpClient(new NotFoundHandler());if(await new AppUpdateService(http).CheckAsync(ct)!=null)throw new InvalidOperationException("No release must not become an update.");
        return "RSA-PSS publisher signature, tampering/wrong key, repository/HTTPS/size/protocol validation, safe ZIP extraction, traversal/duplicate rejection, no-release behavior. Synthetic fixtures only.";
    }
    sealed class NotFoundHandler:HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));}
}
