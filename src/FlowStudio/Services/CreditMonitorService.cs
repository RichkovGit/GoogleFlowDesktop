using System.IO;
using System.Text.Json.Serialization;

namespace FlowStudio.Services;

/// <summary>An observed increase, not a promise about the subscription's allowance or a billing transaction.</summary>
public sealed record CreditIncrease(string Id, long AddedCredits, string Pool, long NewBalance,
    DateTimeOffset ObservedAt, bool DailyRefreshObserved, long? FlowAddedCredits=null, long? AiAddedCredits=null, long? TotalAddedCredits=null)
{
    public string Message => FlowAddedCredits is >0 && AiAddedCredits is >0
        ? $"Баланс Flow вырос на {FlowAddedCredits:N0}, AI — на {AiAddedCredits:N0}."
        : $"Баланс {Pool} увеличился на {AddedCredits:N0}. Сейчас: {NewBalance:N0} кредитов.";
    [JsonIgnore] public bool MatchesDaily50 => AddedCredits == 50 || FlowAddedCredits == 50;
    [JsonIgnore] public bool MatchesMonthly1000 => AddedCredits == 1000 || AiAddedCredits == 1000;
}

/// <summary>
/// Compares real GetCredits snapshots. First sample and account changes only establish a baseline.
/// A refresh timestamp is a server hint; it never creates invented credits or a +50/+1000 timer.
/// Persisted snapshots and claimed action IDs prevent replay across app restarts.
/// </summary>
public sealed class CreditMonitorService
{
    readonly string statePath;
    readonly SemaphoreSlim gate = new(1, 1);
    public CreditMonitorService(string? statePath = null) => this.statePath = statePath ??
        Path.Combine(ProjectStore.AppDirectory, "credit-monitor.json");

    public async Task<CreditIncrease?> ObserveAsync(FlowCreditBalance balance, string accountKey,
        DateTimeOffset observedAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountKey)) throw new ArgumentException("Не выбран аккаунт мониторинга.");
        var account = MediaAutoDownloadService.Hash(accountKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ProjectStore.ReadAsync<MonitorState>(statePath).ConfigureAwait(false) ?? new();
            state.Accounts.TryGetValue(account, out var previous);
            CreditIncrease? increase = null;
            if (previous is not null && observedAt > previous.ObservedAt)
            {
                var flowDelta=PositiveDelta(balance.FlowCredits,previous.Balance.FlowCredits);
                var aiDelta=PositiveDelta(balance.AiCredits,previous.Balance.AiCredits);
                var totalDelta=PositiveDelta(balance.TotalCredits,previous.Balance.TotalCredits);
                if (flowDelta>0 || aiDelta>0 || totalDelta>0)
                {
                    var daily = flowDelta>0 && previous.Balance.NextDailyRefresh is { } expected && expected <= observedAt &&
                        balance.NextDailyRefresh is { } next && next > expected;
                    // Pools are reported independently. Total is never added to component deltas.
                    var pool=flowDelta>0?(aiDelta>0?"Flow и AI":"Flow"):aiDelta>0?"AI":"аккаунта";
                    var added=flowDelta>0||aiDelta>0?checked(flowDelta+aiDelta):totalDelta;
                    var value=pool=="Flow"?balance.FlowCredits!.Value:pool=="AI"?balance.AiCredits!.Value:
                        balance.TotalCredits??balance.FlowCredits??balance.AiCredits??0;
                    var id = MediaAutoDownloadService.Hash(account + "\0" + previous.ObservedAt.ToUnixTimeMilliseconds() +
                        "\0" + observedAt.ToUnixTimeMilliseconds() + "\0" + value);
                    increase = new(id, added, pool, value, observedAt, daily,flowDelta,aiDelta,totalDelta);
                }
            }
            // Ignore stale responses completing after a newer sample.
            if (previous is null || observedAt >= previous.ObservedAt)
            {
                state.Accounts[account] = new(balance, observedAt);
                await ProjectStore.SaveAsync(statePath, state).ConfigureAwait(false);
            }
            return increase;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ClaimActionAsync(string eventId, CancellationToken cancellationToken = default)
    {
        if (eventId.Length != 64 || eventId.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Некорректное событие кредитов.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ProjectStore.ReadAsync<MonitorState>(statePath).ConfigureAwait(false) ?? new();
            if (state.ClaimedActions.Contains(eventId, StringComparer.Ordinal)) return false;
            state.ClaimedActions.Add(eventId);
            // The caller receives each increase only once; retain a bounded action replay history.
            if (state.ClaimedActions.Count > 1024) state.ClaimedActions.RemoveRange(0, state.ClaimedActions.Count - 1024);
            await ProjectStore.SaveAsync(statePath, state).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task ResetBaselineAsync(string accountKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountKey)) return;
        var account = MediaAutoDownloadService.Hash(accountKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ProjectStore.ReadAsync<MonitorState>(statePath).ConfigureAwait(false) ?? new();
            if (state.Accounts.Remove(account)) await ProjectStore.SaveAsync(statePath, state).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    static long PositiveDelta(long? current,long? previous) => current is not null && previous is not null && current>previous ? current.Value-previous.Value : 0;
    public sealed record CreditSnapshot(FlowCreditBalance Balance, DateTimeOffset ObservedAt);
    public sealed class MonitorState
    {
        public Dictionary<string, CreditSnapshot> Accounts { get; set; } = new(StringComparer.Ordinal);
        public List<string> ClaimedActions { get; set; } = [];
    }
}
