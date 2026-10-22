using System.IO;
using System.Security.Cryptography;

namespace FlowStudio.Services;

public static class LibraryFileActionsSelfTest
{
    public static async Task<string> RunAsync(string qaDirectory, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetFullPath(qaDirectory), "library-copy");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source 'оригинал'.mp4");
        var bytes = RandomNumberGenerator.GetBytes(150000);
        await File.WriteAllBytesAsync(source, bytes, cancellationToken).ConfigureAwait(false);
        var outputDirectory = Path.Combine(directory, "saved");
        var first = await LibraryFileActions.CopyOriginalAsync(source, outputDirectory, "../CON: кот\n?", cancellationToken).ConfigureAwait(false);
        var second = await LibraryFileActions.CopyOriginalAsync(source, outputDirectory, "../CON: кот\n?", cancellationToken).ConfigureAwait(false);
        Require(first.Path != second.Path && Path.GetDirectoryName(first.Path) == outputDirectory, "Copies must stay in the chosen folder and never overwrite another copy.");
        Require(Path.GetFileName(first.Path).StartsWith("Flow-", StringComparison.Ordinal) && Path.GetExtension(first.Path) == ".mp4", "Original extension and safe prefix must be preserved.");
        Require(first.Bytes == bytes.Length && (await File.ReadAllBytesAsync(first.Path, cancellationToken).ConfigureAwait(false)).SequenceEqual(bytes), "Copy must preserve the original bytes.");
        Require((await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false)).SequenceEqual(bytes), "Original file must remain unchanged.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var refused = false;
        try { await LibraryFileActions.CopyOriginalAsync(source, outputDirectory, "cancel", canceled.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { refused = true; }
        Require(refused && Directory.EnumerateFiles(outputDirectory).Count() == 2 && !Directory.EnumerateFiles(directory, "*.part", SearchOption.AllDirectories).Any(), "Canceled copies must not leave partial files.");
        Require(LibraryFileActions.SafeStem(".\n\0 /") == "original" && LibraryFileActions.SafeStem(new string('a', 150)).Length == 64, "Filename sanitization must handle empty or overly long titles.");
        return "Manual original-copy preserves bytes/source, sanitizes titles, never overwrites, stays in chosen folder and cleans canceled partials.";
    }
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
