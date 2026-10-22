using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FlowStudio.Services;

public sealed record NvencProbeResult(bool FfmpegAvailable, bool NvencAvailable, string FfmpegPath, int GpuIndex, string Message)
{
    public bool IsAvailable => FfmpegAvailable && NvencAvailable;
}

public sealed record MediaExportProgress(TimeSpan EncodedTime, long Frames, string Speed, bool IsComplete);

public sealed record MediaExportResult(string OutputPath, long SizeBytes, TimeSpan Elapsed, int GpuIndex);

/// <summary>
/// Exports compatible local clips using CUDA decoding and NVIDIA H.264 NVENC.
/// There is deliberately no software video encoder fallback. Audio is copied.
/// </summary>
public sealed partial class MediaExportService
{
    private static readonly HashSet<string> SupportedInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".ts", ".mts", ".m2ts"
    };

    public string FfmpegPath { get; }
    public int NvidiaGpuIndex { get; }

    public MediaExportService(string? ffmpegPath = null, int nvidiaGpuIndex = 0)
    {
        if (nvidiaGpuIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(nvidiaGpuIndex));
        FfmpegPath = FindFfmpeg(ffmpegPath);
        NvidiaGpuIndex = nvidiaGpuIndex;
    }

    /// <summary>Runs a real one-frame encode, verifying driver, CUDA and NVENC support.</summary>
    public async Task<NvencProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FfmpegPath))
            return new(false, false, FfmpegPath, NvidiaGpuIndex,
                "FFmpeg не найден. Укажите путь к ffmpeg.exe с поддержкой NVIDIA NVENC.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var result = await RunAsync(new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-init_hw_device", $"cuda=flow:{NvidiaGpuIndex.ToString(CultureInfo.InvariantCulture)}",
                "-filter_hw_device", "flow", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1",
                "-frames:v", "1", "-vf", "format=nv12,hwupload_cuda",
                "-c:v", "h264_nvenc", "-gpu", NvidiaGpuIndex.ToString(CultureInfo.InvariantCulture),
                "-preset", "p4", "-f", "null", "-"
            }, null, timeout.Token).ConfigureAwait(false);
            return result.ExitCode == 0
                ? new(true, true, FfmpegPath, NvidiaGpuIndex, $"NVENC готов · NVIDIA GPU {NvidiaGpuIndex}")
                : new(true, false, FfmpegPath, NvidiaGpuIndex,
                    "NVENC недоступен: " + DescribeFailure(result.Error));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new(true, false, FfmpegPath, NvidiaGpuIndex, "Проверка NVENC превысила время ожидания.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return new(File.Exists(FfmpegPath), false, FfmpegPath, NvidiaGpuIndex,
                $"Не удалось проверить NVENC: {exception.Message}");
        }
    }

    /// <summary>
    /// Clips must have matching resolution, codec parameters, frame rate and audio layout.
    /// Writes a new MP4 atomically and never overwrites an existing destination.
    /// </summary>
    public async Task<MediaExportResult> ExportConcatNvencAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        IProgress<MediaExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        if (inputPaths.Count == 0)
            throw new ArgumentException("Добавьте хотя бы один локальный видеоклип.", nameof(inputPaths));
        var inputs = inputPaths.Select(ValidateInput).ToArray();
        var destination = ValidateLocalPath(outputPath);
        if (!string.Equals(Path.GetExtension(destination), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Для NVENC-экспорта выберите файл .mp4.", nameof(outputPath));
        if (File.Exists(destination))
            throw new IOException("Файл назначения уже существует. Выберите новое имя.");
        if (inputs.Contains(destination, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Файл назначения совпадает с исходным клипом.", nameof(outputPath));
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Не удалось определить папку экспорта.", nameof(outputPath));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Папка экспорта не существует.");

        var probe = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.IsAvailable)
            throw new InvalidOperationException(probe.Message + " Программный кодировщик не используется.");

        var id = Guid.NewGuid().ToString("N");
        var manifest = Path.Combine(directory, $".flow-{id}.ffconcat");
        var temporaryOutput = Path.Combine(directory, $".flow-{id}.mp4");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var lines = new[] { "ffconcat version 1.0" }.Concat(inputs.Select(input => "file '" + EscapeConcatPath(input) + "'"));
            await File.WriteAllLinesAsync(manifest, lines, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var result = await RunAsync(new[]
            {
                "-hide_banner", "-loglevel", "warning", "-nostdin", "-n",
                "-hwaccel", "cuda", "-hwaccel_device", NvidiaGpuIndex.ToString(CultureInfo.InvariantCulture),
                "-hwaccel_output_format", "cuda", "-extra_hw_frames", "8",
                "-protocol_whitelist", "file", "-f", "concat", "-safe", "0", "-i", manifest,
                "-map", "0:v:0", "-map", "0:a?",
                "-c:v", "h264_nvenc", "-gpu", NvidiaGpuIndex.ToString(CultureInfo.InvariantCulture),
                "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", "0",
                "-c:a", "copy", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats",
                temporaryOutput
            }, progress, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("NVENC-экспорт не завершён. " + DescribeFailure(result.Error)
                    + " Используйте клипы с одинаковым разрешением, частотой кадров и звуком. Программное кодирование отключено.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length == 0)
                throw new IOException("FFmpeg не создал готовое видео.");
            // The rename stays on the destination volume. An existing file is never replaced.
            File.Move(temporaryOutput, destination, overwrite: false);
            stopwatch.Stop();
            return new(destination, new FileInfo(destination).Length, stopwatch.Elapsed, NvidiaGpuIndex);
        }
        finally
        {
            DeleteTemporaryFile(manifest);
            DeleteTemporaryFile(temporaryOutput);
        }
    }

    private async Task<(int ExitCode, string Error)> RunAsync(
        IEnumerable<string> arguments,
        IProgress<MediaExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить FFmpeg.");
        var stderrTask = ReadErrorTailAsync(process.StandardError);
        var stdoutTask = ReadProgressAsync(process.StandardOutput, progress);
        using var registration = cancellationToken.Register(() => TryKill(process));
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (process.ExitCode, await stderrTask.ConfigureAwait(false));
        }
        catch
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // Drain both streams before disposal so cancellation cannot leave FFmpeg behind.
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            throw;
        }
    }

    private static async Task ReadProgressAsync(StreamReader reader, IProgress<MediaExportProgress>? progress)
    {
        var encodedTime = TimeSpan.Zero;
        long frames = 0;
        var speed = string.Empty;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = line[..separator];
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds):
                    if (microseconds >= 0 && microseconds <= TimeSpan.MaxValue.Ticks / 10)
                        encodedTime = TimeSpan.FromTicks(microseconds * 10);
                    break;
                case "frame":
                    _ = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out frames);
                    break;
                case "speed":
                    speed = value;
                    break;
                case "progress":
                    progress?.Report(new(encodedTime, frames, speed, value == "end"));
                    break;
            }
        }
    }

    private static async Task<string> ReadErrorTailAsync(StreamReader reader)
    {
        const int maximumCharacters = 32768;
        var tail = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > maximumCharacters)
                tail.Remove(0, tail.Length - maximumCharacters);
        }
        return tail.ToString();
    }

    private static string FindFfmpeg(string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
            return Path.GetFullPath(requestedPath);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "ffmpeg.exe")
        };
        // Avoid searching a user-controlled current working directory or invoking a shell.
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string ValidateInput(string input)
    {
        var path = ValidateLocalPath(input);
        if (!SupportedInputExtensions.Contains(Path.GetExtension(path)))
            throw new ArgumentException("Выберите локальный видеофайл MP4, MOV, MKV, WebM, AVI или TS.", nameof(input));
        if (!File.Exists(path))
            throw new FileNotFoundException("Исходный клип не найден.", path);
        if (new FileInfo(path).Length == 0)
            throw new IOException($"Исходный клип пуст: {Path.GetFileName(path)}");
        return path;
    }

    private static string ValidateLocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Ожидается полный путь к локальному файлу.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Экспорт принимает файлы с локального диска.", nameof(path));
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network)
            throw new ArgumentException("Экспорт принимает файлы с локального диска.", nameof(path));
        return fullPath;
    }

    private static string EscapeConcatPath(string path)
        => path.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);

    private static string DescribeFailure(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "FFmpeg завершился с ошибкой; проверьте драйвер NVIDIA и совместимость видео.";
        var lines = error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var message = string.Join(" ", lines.TakeLast(4));
        return message.Length <= 1200 ? message : message[^1200..];
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
