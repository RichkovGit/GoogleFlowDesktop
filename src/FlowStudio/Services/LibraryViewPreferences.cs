using System.IO;

namespace FlowStudio.Services;

/// <summary>Native local library presentation only; never claims to change Flow account settings.</summary>
public sealed class LibraryViewPreferences
{
    public string TypeFilter { get; set; } = "all";
    public string Sort { get; set; } = "newest";
    public string Layout { get; set; } = "grid";
    public string CardSize { get; set; } = "medium";
    public bool ShowCardInfo { get; set; } = true;
    public bool FavoritesOnly { get; set; }
    public bool PreviewMuted { get; set; }
    public bool HoverAudioEnabled { get; set; }
    public bool ClearPromptAfterSuccess { get; set; }

    static string FilePath => Path.Combine(ProjectStore.AppDirectory, "library-view-preferences.json");
    public static async Task<LibraryViewPreferences> LoadAsync()
    {
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 65536) return new();
        var value = await ProjectStore.ReadAsync<LibraryViewPreferences>(FilePath) ?? new();
        value.Normalize(); return value;
    }
    public Task SaveAsync() { Normalize(); return ProjectStore.SaveAsync(FilePath, this); }
    void Normalize()
    {
        if (TypeFilter is not ("all" or "video" or "image")) TypeFilter = "all";
        if (Sort is not ("newest" or "oldest" or "name")) Sort = "newest";
        if (Layout is not ("grid" or "batch")) Layout = "grid";
        if (CardSize is not ("small" or "medium" or "large")) CardSize = "medium";
    }
}
