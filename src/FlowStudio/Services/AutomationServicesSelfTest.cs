using System.IO;
using System.Text.Json;

namespace FlowStudio.Services;

/// <summary>Synthetic catalog, local copy and actual-snapshot credit behavior. No network, UI notifications or user secrets.</summary>
public static class AutomationServicesSelfTest
{
    public static async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "FlowStudio-automation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestCatalog();
            await TestCreditsAsync(root, cancellationToken).ConfigureAwait(false);
            await TestDownloadsAsync(root, cancellationToken).ConfigureAwait(false);
            var prefs = new UserPreferences();
            prefs.Validate();
            Require(!prefs.CreditActionsEnabled && !prefs.AutoDownloadEnabled && !prefs.AutoStitchEnabled, "Side effects must be opt-in.");
            prefs.CreditActionsEnabled = true;
            RequireThrows(prefs.Validate, "Missing automatic-generation details must fail validation.");
            return "Models grouped by family, exact text-only usages and duration/aspect/tier filtering; credit baseline/restart/stale/account/refresh/action replay; atomic collision-safe deduplicated copies and cancellation.";
        }
        finally
        {
            // This exact directory is created by this self-test beneath the known OS temporary folder.
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static object?[] Msg(params (int Field, object? Value)[] entries)
    {
        var result = new object?[entries.Max(entry => entry.Field)];
        foreach(var entry in entries) result[entry.Field - 1] = entry.Value;
        return result;
    }
    static object?[] Usage(string key, int seconds, int[] input, bool hidden=false) => Msg(
        (1,key), (5,new object?[] { new object?[]{1,Msg((1,Msg((2,10))))}, new object?[]{2,Msg((2,Array.Empty<object>()))} }),
        (8,Msg((1,new object?[]{Msg((1,input))}))), (13,Msg((1,new[]{1,2}))), (17,seconds), (25,hidden));

    static void TestCatalog()
    {
        var family = Msg((1,"Omni 1.1 Flash"), (2,new object?[] {
            Usage("abra_t2v_4s",4,[1]), Usage("abra_t2v_8s",8,[1]), Usage("abra_i2v_4s",4,[1,5]),
            Usage("hidden_usage",6,[1],true), Usage("abra_t2v_4s",4,[1]) }), (4,"omni_1_1"));
        var hidden = Msg((1,"Hidden model"),(2,new object?[]{Usage("hidden_family",4,[1])}),(4,"hidden"),(6,true));
        var json = JsonSerializer.Serialize(Msg((1,Msg((5,new object?[]{family,hidden})))));
        var catalog = FlowModelCatalogService.ParseCatalog(json);
        Require(catalog.Count==3,"Duplicate and hidden usage/family should be removed.");
        var variants=FlowModelCatalogService.GetTextModels(catalog,"video","16:9",1);
        Require(variants.Count==2 && variants[0].DurationSeconds==4 && variants[1].DurationSeconds==8,"Only pure-text variants should remain ordered by duration.");
        Require(variants.Select(model=>model.SelectionLabel).Distinct().Count()==2,"Durations must make otherwise identical model labels distinct.");
        Require(variants[0].CreditsForTier(1)==10 && !variants[0].IsAvailableForTier(2),"Tier prices and unavailability must be parsed.");
        Require(FlowModelCatalogService.GetTextModels(catalog,"video","1:1",1).Count==0,"Unsupported aspect must not be selected.");
        Require(FlowModelCatalogService.GetTextModels(catalog,"video","16:9",2).Count==0,"Unavailable credit tier must not be selectable.");
        var families=FlowModelCatalogService.GetFamilies(catalog,"video","16:9",1);
        Require(families.Count==1 && families[0].Variants.Count==2,"One family with two lengths should replace repeated model names.");
        var free=Msg((1,"Free"),(2,new object?[]{Msg((1,"free-text"),(5,new object?[]{new object?[]{1,Array.Empty<object>()}}))}),(4,"free"));
        var freeCatalog=FlowModelCatalogService.ParseCatalog(JsonSerializer.Serialize(Msg((1,Msg((6,new object?[]{free}))))));
        Require(freeCatalog.Single().CreditsForTier(1)==0 && freeCatalog.Single().IsAvailableForTier(1),"A present zero-cost protobuf scalar default must not mean unavailable.");
    }

    static FlowCreditBalance Balance(long? total,long? flow,DateTimeOffset? next=null) => new(total,null,flow,1,2,2,next);
    static async Task TestCreditsAsync(string root,CancellationToken cancellationToken)
    {
        var path=Path.Combine(root,"credits.json");
        var monitor=new CreditMonitorService(path);
        var t=new DateTimeOffset(2026,10,20,10,0,0,TimeSpan.Zero);
        var daily=t.AddHours(1);
        Require(await monitor.ObserveAsync(Balance(197,197,daily),"account-one",t,cancellationToken) is null,"First sample establishes baseline.");
        Require(await monitor.ObserveAsync(Balance(197,197,daily),"account-one",t.AddMinutes(1),cancellationToken) is null,"Equal balance must be silent.");
        Require(await monitor.ObserveAsync(Balance(187,187,daily),"account-one",t.AddMinutes(2),cancellationToken) is null,"Spending must be silent.");
        var increase=await monitor.ObserveAsync(Balance(237,237,daily.AddDays(1)),"account-one",daily.AddMinutes(1),cancellationToken);
        Require(increase is {AddedCredits:50,DailyRefreshObserved:true,MatchesDaily50:true},"Observed increase + advancing real refresh must be recognized.");
        Require(await monitor.ClaimActionAsync(increase!.Id,cancellationToken),"New action should be claimed once.");
        var restarted=new CreditMonitorService(path);
        Require(!await restarted.ClaimActionAsync(increase.Id,cancellationToken),"Restart must not replay action.");
        Require(await restarted.ObserveAsync(Balance(237,237,daily.AddDays(1)),"account-one",daily.AddMinutes(2),cancellationToken) is null,"Restart must preserve baseline.");
        Require(await restarted.ObserveAsync(Balance(1000,1000),"account-two",daily.AddMinutes(3),cancellationToken) is null,"Another account must get a new baseline.");
        Require(await restarted.ObserveAsync(Balance(1237,1237),"account-one",t,cancellationToken) is null,"Stale responses must not mutate baseline or notify.");
        increase=await restarted.ObserveAsync(Balance(1237,1237,daily.AddDays(1)),"account-one",daily.AddMinutes(4),cancellationToken);
        Require(increase is {AddedCredits:1000,DailyRefreshObserved:false,MatchesMonthly1000:true},"A +1000 balance observation is factual, not an invented monthly refresh.");
        await monitor.ObserveAsync(new(1000,1000,50,1,2,2,null),"separate-pools",t,cancellationToken);
        increase=await monitor.ObserveAsync(new(2000,2000,50,1,2,2,null),"separate-pools",t.AddMinutes(1),cancellationToken);
        Require(increase is {AddedCredits:1000,Pool:"AI",MatchesMonthly1000:true},"AI/monthly pool increases must be detected even when the Flow pool does not change.");
        await restarted.ResetBaselineAsync("account-one",cancellationToken);
        Require(await restarted.ObserveAsync(Balance(5000,5000),"account-one",daily.AddMinutes(5),cancellationToken) is null,"Reset must not mistake reauthorization for a grant.");
        Require(!await File.ReadAllTextAsync(path,cancellationToken).ContinueWith(task=>task.Result.Contains("account-one",StringComparison.Ordinal),cancellationToken),"Plain account key must not be persisted.");
    }

    static async Task TestDownloadsAsync(string root,CancellationToken cancellationToken)
    {
        var source=Path.Combine(root,"test.mp4");
        await File.WriteAllBytesAsync(source,Enumerable.Range(0,4096).Select(number=>(byte)number).ToArray(),cancellationToken);
        var folder=Path.Combine(root,"Сохранённые видео");
        var path=Path.Combine(root,"downloads.json");
        var service=new MediaAutoDownloadService(path);
        var first=await service.SaveVideoAsync(source,"media/first",folder,cancellationToken);
        Require(!first.AlreadySaved && File.Exists(first.Path) && first.Bytes==4096,"First generated video should be copied.");
        var second=await new MediaAutoDownloadService(path).SaveVideoAsync(source,"media/first",folder,cancellationToken);
        Require(second.AlreadySaved && second.Path==first.Path && Directory.GetFiles(folder).Length==1,"Restart must deduplicate the same cloud video.");
        File.Delete(path);
        var recovered=await service.SaveVideoAsync(source,"media/first",folder,cancellationToken);
        Require(recovered.AlreadySaved && recovered.Path==first.Path,"Interrupted ledger save must adopt an identical completed output.");
        var key=MediaAutoDownloadService.Hash("media/collision\0"+Path.GetFullPath(folder).ToUpperInvariant());
        var collision=Path.Combine(folder,$"Flow-{key[..20]}.mp4");
        await File.WriteAllTextAsync(collision,"belongs to user",cancellationToken);
        var third=await service.SaveVideoAsync(source,"media/collision",folder,cancellationToken);
        Require(third.Path!=collision && await File.ReadAllTextAsync(collision,cancellationToken)=="belongs to user","Unrelated existing output must never be overwritten.");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        try { await service.SaveVideoAsync(source,"media/canceled",folder,canceled.Token);throw new InvalidOperationException("Cancellation should be observed."); }
        catch(OperationCanceledException) { }
        Require(!Directory.GetFiles(folder).Any(file=>file.EndsWith(".part",StringComparison.Ordinal)),"Canceled copy must not leave a partial file.");
    }

    static void Require(bool success,string message) { if(!success) throw new InvalidOperationException(message); }
    static void RequireThrows(Action action,string message)
    {
        try { action(); } catch(InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
}
