using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FlowStudio.Services;

public sealed record MediaAutoDownloadResult(string Path, bool AlreadySaved, long Bytes);

/// <summary>Copies a completed generated video to a user-selected folder; never submits generation or downloads old library items.</summary>
public sealed class MediaAutoDownloadService
{
    readonly string ledgerPath;
    readonly SemaphoreSlim gate = new(1, 1);
    public MediaAutoDownloadService(string? ledgerPath = null) => this.ledgerPath = ledgerPath ??
        System.IO.Path.Combine(ProjectStore.AppDirectory, "video-downloads.json");

    public async Task<MediaAutoDownloadResult> SaveVideoAsync(string sourcePath, string mediaName, string folder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaName) || mediaName.Length > 4096) throw new ArgumentException("Отсутствует идентификатор созданного видео.");
        if (!System.IO.Path.IsPathFullyQualified(folder)) throw new ArgumentException("Выберите полную папку сохранения.");
        var destinationFolder = System.IO.Path.GetFullPath(folder);
        var source = System.IO.Path.GetFullPath(sourcePath);
        if (!ProjectStore.IsVideo(source) || !File.Exists(source)) throw new FileNotFoundException("Готовое видео не найдено.");
        var info = new FileInfo(source);
        if (info.Length <= 0) throw new InvalidDataException("Нельзя сохранить пустое видео.");
        var key = Hash(mediaName + "\0" + destinationFolder.ToUpperInvariant());
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ledger = await ProjectStore.ReadAsync<DownloadLedger>(ledgerPath).ConfigureAwait(false) ?? new();
            if (ledger.Entries.TryGetValue(key, out var previous) &&
                string.Equals(System.IO.Path.GetDirectoryName(previous.Path), destinationFolder, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(previous.Path) && new FileInfo(previous.Path).Length == previous.Bytes)
                return new(previous.Path, true, previous.Bytes);
            Directory.CreateDirectory(destinationFolder);
            var extension = System.IO.Path.GetExtension(source).ToLowerInvariant();
            var filename = $"Flow-{key[..20]}{extension}";
            var destination = System.IO.Path.Combine(destinationFolder, filename);
            if (File.Exists(destination))
            {
                // Recover a completed copy after a crash between rename and ledger save.
                // An unrelated file at the deterministic name is never overwritten/adopted.
                if (new FileInfo(destination).Length == info.Length && await SameContentAsync(source, destination, cancellationToken).ConfigureAwait(false))
                {
                    ledger.Entries[key] = new(destination, info.Length, DateTimeOffset.UtcNow);
                    await ProjectStore.SaveAsync(ledgerPath, ledger).ConfigureAwait(false);
                    return new(destination, true, info.Length);
                }
                destination = System.IO.Path.Combine(destinationFolder, $"Flow-{key[..20]}-{Guid.NewGuid():N}{extension}");
            }
            // CreateNew prevents another process's output from being overwritten.
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
            temporary = null;
            var bytes = new FileInfo(destination).Length;
            ledger.Entries[key] = new(destination, bytes, DateTimeOffset.UtcNow);
            await ProjectStore.SaveAsync(ledgerPath, ledger).ConfigureAwait(false);
            return new(destination, false, bytes);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            gate.Release();
        }
    }

    static async Task<bool> SameContentAsync(string left, string right, CancellationToken cancellationToken)
    {
        await using var leftFile = File.OpenRead(left);
        await using var rightFile = File.OpenRead(right);
        var leftHash = await SHA256.HashDataAsync(leftFile, cancellationToken).ConfigureAwait(false);
        var rightHash = await SHA256.HashDataAsync(rightFile, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public sealed record DownloadEntry(string Path, long Bytes, DateTimeOffset SavedAt);
    public sealed class DownloadLedger { public Dictionary<string, DownloadEntry> Entries { get; set; } = new(StringComparer.Ordinal); }
}
