using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace FlowStudio.Services;

/// <summary>Refreshes Google's own bootstrap using an already authorized app session. Never signs in or retries a generation.</summary>
public static class FlowSessionRefreshService
{
    static readonly string[] Keys = ["awbSEf", "SNlM0e", "S06Grb", "cfb2h", "FdrFJe", "xZbWve"];
    public static async Task<FlowBrowserSession> RefreshAsync(FlowBrowserSession session, CancellationToken ct=default)
    {
        using var handler=new HttpClientHandler { AllowAutoRedirect=false, UseCookies=false };
        using var client=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(30) };
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var cookies=session.Cookies.ToList();
        var uri=new Uri("https://flow.google.com/");
        for(var attempt=0;attempt<4;attempt++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            request.Headers.TryAddWithoutValidation("Cookie",FlowCookieAuthHandler.BuildCookieHeader(cookies,uri,DateTimeOffset.UtcNow));
            if(!string.IsNullOrWhiteSpace(session.UserAgent))request.Headers.TryAddWithoutValidation("User-Agent",session.UserAgent);
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if(response.Headers.TryGetValues("Set-Cookie",out var additions))
                foreach(var addition in additions) MergeCookie(cookies,uri,addition);
            if((int)response.StatusCode is >=300 and <400)
            {
                if(response.Headers.Location is null||!Uri.TryCreate(uri,response.Headers.Location,out var next)||next.Scheme!="https"||next.Host!="flow.google.com"||!next.IsDefaultPort)
                    throw new FlowBrowserLoginException("Сеанс Google истёк. Повторите вход в аккаунт.");
                uri=next;continue;
            }
            if(!response.IsSuccessStatusCode)throw new FlowBrowserLoginException($"Не удалось обновить сеанс Google (HTTP {(int)response.StatusCode}).");
            await using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body=new MemoryStream();var buffer=new byte[16384];int count;
            while((count=await stream.ReadAsync(buffer,deadline.Token))>0)
            {if(body.Length+count>8*1024*1024)throw new FlowBrowserLoginException("Страница входа Flow превышает допустимый размер.");body.Write(buffer,0,count);}
            var wiz=ReadBootstrap(Encoding.UTF8.GetString(body.GetBuffer(),0,(int)body.Length));
            var refreshed=new FlowBrowserSession(cookies,wiz,session.UserAgent,DateTimeOffset.UtcNow);
            if(!refreshed.HasGoogleSession||!wiz.TryGetValue("SNlM0e",out var csrf)||string.IsNullOrWhiteSpace(csrf))
                throw new FlowBrowserLoginException("Google требует повторный вход. Откройте вход в настройках аккаунта.");
            return refreshed;
        }
        throw new FlowBrowserLoginException("Flow перенаправляет вход слишком много раз. Повторите подключение аккаунта.");
    }

    internal static IReadOnlyDictionary<string,string?> ReadBootstrap(string html)
    {
        var start=html.IndexOf("WIZ_global_data",StringComparison.Ordinal);
        if(start<0)throw new FlowBrowserLoginException("Flow не вернул параметры сеанса Google.");
        var equals=html.IndexOf('=',start);var opening=equals<0?-1:html.IndexOf('{',equals);
        if(opening<0||opening-equals>32)throw new FlowBrowserLoginException("Изменился формат входа Flow.");
        try
        {
            var reader=new Utf8JsonReader(Encoding.UTF8.GetBytes(html[opening..]));
            using var json=JsonDocument.ParseValue(ref reader);
            var result=new Dictionary<string,string?>(StringComparer.Ordinal);
            foreach(var key in Keys)
                if(json.RootElement.TryGetProperty(key,out var value))result[key]=value.ValueKind switch
                {JsonValueKind.String=>value.GetString(),JsonValueKind.True=>"true",JsonValueKind.False=>"false",JsonValueKind.Number=>value.GetRawText(),_=>null};
            return result;
        }
        catch(JsonException){throw new FlowBrowserLoginException("Изменился формат параметров сеанса Flow.");}
    }
    internal static void MergeCookie(List<FlowSessionCookie> cookies,Uri origin,string text)
    {
        try
        {
            var jar=new CookieContainer();jar.SetCookies(origin,text);
            foreach(Cookie cookie in jar.GetAllCookies())
            {
                if(!cookies.Any(c=>c.Name==cookie.Name))continue;
                if(cookie.Domain.TrimStart('.') is not ("google.com" or "flow.google.com"))continue;
                cookies.RemoveAll(c=>c.Name==cookie.Name&&c.Domain.TrimStart('.')==cookie.Domain.TrimStart('.')&&c.Path==cookie.Path);
                cookies.Add(new(cookie.Name,cookie.Value,cookie.Domain,cookie.Path,cookie.Expires==DateTime.MinValue?null:new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds(),cookie.Secure,cookie.HttpOnly,null));
            }
        }
        catch(CookieException) { }
    }
}
