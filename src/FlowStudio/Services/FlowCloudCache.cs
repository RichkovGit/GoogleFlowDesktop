using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FlowStudio.Services;

/// <summary>
/// Stable local paths for cloud projects already authorized by a fresh account listing.
/// This helper never lists cached projects, stores remote URLs, or modifies project files.
/// A path in this directory is not evidence that the current account can access a project.
/// </summary>
public static class FlowCloudCache
{
    public static string CacheDirectory => Path.GetFullPath(Path.Combine(ProjectStore.AppDirectory, "CloudCache"));

    /// <summary>
    /// Returns the existing local workspace path. Callers must preserve its drafts,
    /// storyboard, and imported media when refreshing remote metadata.
    /// ProjectStore.SaveAsync supplies atomic writes when the native workspace is saved.
    /// </summary>
    public static string GetProjectPath(string projectId) =>
        Path.Combine(GetProjectDirectory(projectId), "project.flowstudio");

    public static string GetMediaDirectory(string projectId) =>
        Path.Combine(GetProjectDirectory(projectId), "media");

    /// <summary>A stable download destination; the identifier is hashed rather than used as a filename.</summary>
    public static string GetMediaPath(string projectId, string mediaId, string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var normalizedExtension = extension.ToLowerInvariant();
        if (normalizedExtension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or
            ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi"))
            throw new ArgumentException("Неподдерживаемый формат материала Flow.", nameof(extension));
        return Path.Combine(GetMediaDirectory(projectId), GetAssetId(projectId, mediaId) + normalizedExtension);
    }

    /// <summary>Use as Asset.Id to merge repeated syncs without duplicating the same cloud media.</summary>
    public static string GetAssetId(string projectId, string mediaId)
    {
        var id = NormalizeProjectId(projectId);
        if (string.IsNullOrWhiteSpace(mediaId) || mediaId.Length > 2048 || mediaId.Any(char.IsControl) ||
            Uri.TryCreate(mediaId, UriKind.Absolute, out var address) && address.Scheme is "http" or "https")
            throw new ArgumentException("Для кэша нужен идентификатор материала Flow, а не временная ссылка.", nameof(mediaId));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("FlowStudio.CloudMedia.v1\0" + id + "\0" + mediaId));
        return "flow-" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>Checks lexical containment only. It never authorizes access for the active account.</summary>
    public static bool IsCloudCachePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var root = CacheDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string GetProjectDirectory(string projectId) =>
        Path.Combine(CacheDirectory, NormalizeProjectId(projectId));

    private static string NormalizeProjectId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Не указан проект Flow.", nameof(value));
        var text = value.Trim();
        if (text.StartsWith("projects/", StringComparison.Ordinal)) text = text["projects/".Length..];
        // ParseExact avoids paths, URLs, brace forms, and ambiguous compact identifiers.
        if (text.Length != 36 || !Guid.TryParseExact(text, "D", out var id))
            throw new ArgumentException("Некорректный идентификатор проекта Flow.", nameof(value));
        return id.ToString("D");
    }
}
