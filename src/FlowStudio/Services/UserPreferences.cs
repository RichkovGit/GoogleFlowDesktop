using System.IO;

namespace FlowStudio.Services;

/// <summary>User choices only. Credentials and remote signed URLs never belong here.</summary>
public sealed class UserPreferences
{
    public bool AutoLogin { get; set; } = true;
    public bool AutoDownloadEnabled { get; set; }
    public string DownloadFolder { get; set; } = "";
    public bool AutoStitchEnabled { get; set; }
    public bool CreditNotificationsEnabled { get; set; } = true;
    public int CreditPollingMinutes { get; set; } = 5;
    public bool CreditActionsEnabled { get; set; }
    public bool CreditActionsDailyRefreshOnly { get; set; }
    public string CreditActionPrompt { get; set; } = "";
    public string CreditActionModelKey { get; set; } = "";
    public string CreditActionKind { get; set; } = "video";
    public int CreditActionMaximumGenerations { get; set; } = 1;
    public double CreditActionMaximumCredits { get; set; } = 50;
    public string PreferredEncoder { get; set; } = "auto";
    public bool AllowSoftwareEncoding { get; set; }

    public static string FilePath => Path.Combine(ProjectStore.AppDirectory, "preferences.json");
    public static async Task<UserPreferences> LoadAsync() => await ProjectStore.ReadAsync<UserPreferences>(FilePath) ?? new();
    public Task SaveAsync() => ProjectStore.SaveAsync(FilePath, this);

    public void Validate()
    {
        if (CreditPollingMinutes is < 1 or > 60) throw new InvalidOperationException("Интервал проверки кредитов: от 1 до 60 минут.");
        if (AutoDownloadEnabled && (string.IsNullOrWhiteSpace(DownloadFolder) || !Path.IsPathFullyQualified(DownloadFolder)))
            throw new InvalidOperationException("Выберите полную папку для автоматического сохранения видео.");
        if (AutoStitchEnabled && !AutoDownloadEnabled) throw new InvalidOperationException("Для автосклейки сначала включите автосохранение видео.");
        if (CreditActionsEnabled)
        {
            if (string.IsNullOrWhiteSpace(CreditActionPrompt) || CreditActionPrompt.Length > 30000 || string.IsNullOrWhiteSpace(CreditActionModelKey))
                throw new InvalidOperationException("Для действия при зачислении укажите описание и модель Flow.");
            if (CreditActionKind is not ("video" or "image")) throw new InvalidOperationException("Неверный тип автоматической генерации.");
            if (CreditActionMaximumGenerations is < 1 or > 4 || !double.IsFinite(CreditActionMaximumCredits) || CreditActionMaximumCredits is <= 0 or > 1000)
                throw new InvalidOperationException("Ограничьте действие: 1–4 генерации и бюджет до 1000 кредитов.");
        }
    }
}
