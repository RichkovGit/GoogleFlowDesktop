namespace FlowStudio.Services;
public static class FlowBrowserRuntimeSelfTest
{
    public static async Task<string> RunAsync(CancellationToken ct=default)
    {
        foreach(var value in new[]{"http://flow.google.com"+FlowBrowserRuntimeService.BatchPath,"https://flow.google.com.evil.test"+FlowBrowserRuntimeService.BatchPath,"https://flow.google.com:444"+FlowBrowserRuntimeService.BatchPath,"https://flow.google.com/other","https://user@flow.google.com"+FlowBrowserRuntimeService.AgentPath})
        {var refused=false;try{FlowBrowserRuntimeService.ValidateActionEndpoint(new(value));}catch(ArgumentException){refused=true;}if(!refused)throw new InvalidOperationException("Endpoint boundary failed.");}
        FlowBrowserRuntimeService.ValidateActionEndpoint(new("https://flow.google.com"+FlowBrowserRuntimeService.AgentPath));
        var source=FlowBrowserRuntimeService.CountryCompatibilityScript;
        if(source.Contains("value[31]")||source.Contains("awbSEf")||source.Contains("signout")||source.Contains("grecaptcha"))throw new InvalidOperationException("Compatibility must not change age/authentication/security.");
        if(!FlowBrowserRuntimeService.BuildActionTokenExpression("VIDEO_GENERATION").Contains("SITE_UI_REQUIRED"))throw new InvalidOperationException("Protected SDK must remain protected.");
        var fields=Enumerable.Repeat<object?>(null,32).ToArray();fields[30]=false;fields[31]=false;
        var payload=System.Text.Json.JsonSerializer.Serialize(fields);
        var frame=System.Text.Json.JsonSerializer.Serialize(new object?[]{new object?[]{"wrb.fr","cPZSdc",payload}});
        var raw=")]}'\n\n"+(frame.Length+1)+"\n"+frame;
        var changed=FlowBrowserRuntimeService.PatchCountryResponse(raw);
        var decoded=FlowCookieAuthHandler.DecodeBatchResponse(changed,"cPZSdc");
        using(var json=System.Text.Json.JsonDocument.Parse(decoded.Payload!))
            if(!json.RootElement[30].GetBoolean()||json.RootElement[31].GetBoolean())throw new InvalidOperationException("Country-only response framing or unchanged age field failed.");
        if(FlowBrowserRuntimeService.PatchCountryResponse("unrelated")!="unrelated")throw new InvalidOperationException("Unrelated response changed.");
        await using var runtime=new FlowBrowserRuntimeService();var invalid=false;
        try{await runtime.SubmitVideoUiAsync(new("invalid","invalid","test"),new("image","id","unsupported","key"),ct);}catch(FlowBrowserRuntimeException){invalid=true;}
        if(!invalid)throw new InvalidOperationException("Unsupported UI generation must fail before starting Chrome.");
        return "Exact Flow origin/paths, protected SDK refusal, country-only compatibility, unsupported request preflight; no browser/account/network/paid request.";
    }
}
