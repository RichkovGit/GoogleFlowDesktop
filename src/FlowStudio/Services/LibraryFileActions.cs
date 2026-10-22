using System.IO;

namespace FlowStudio.Services;

public sealed record LibraryCopyResult(string Path, long Bytes);

/// <summary>Copies a selected original atomically to the folder chosen by the user.</summary>
public static class LibraryFileActions
{
    public static async Task<LibraryCopyResult> CopyOriginalAsync(string sourcePath, string destinationFolder, string displayName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(destinationFolder)) throw new ArgumentException("Выберите полный путь к оригиналу и папке сохранения.");
        var source = Path.GetFullPath(sourcePath); var directory = Path.GetFullPath(destinationFolder);
        if (!ProjectStore.IsMedia(source) || !File.Exists(source)) throw new FileNotFoundException("Оригинал изображения или видео не найден.", source);
        var info = new FileInfo(source);
        if (info.Length == 0) throw new InvalidDataException("Нельзя сохранить пустой оригинал.");
        Directory.CreateDirectory(directory);
        var name = SafeStem(displayName);
        var destination = Path.Combine(directory, $"Flow-{name}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{Path.GetExtension(source).ToLowerInvariant()}");
        var temporary = destination + ".part";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            return new(destination, new FileInfo(destination).Length);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static string SafeStem(string? value)
    {
        var cleaned = string.Concat((value ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c))).Trim(' ', '.');
        if (cleaned.Length > 64) cleaned = cleaned[..64].TrimEnd(' ', '.');
        // Every exported name has the Flow- prefix, so reserved DOS device names
        // cannot become the entire filename even if they came from an asset title.
        return string.IsNullOrWhiteSpace(cleaned) ? "original" : cleaned;
    }
}
