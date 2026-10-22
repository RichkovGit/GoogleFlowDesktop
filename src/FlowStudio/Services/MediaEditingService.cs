using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FlowStudio.Services;

public enum MediaEncoderPreference { Auto, NvidiaNvenc, AmdAmf, IntelQsv, Cpu }
public sealed record MediaEncoderCapability(MediaEncoderPreference Preference, string EncoderName, string DisplayName, bool IsHardware, bool Available, string Message);
public sealed record MediaTimelineClip(string Path, double TrimStartSeconds = 0, double? TrimEndSeconds = null, double Volume = 1, bool Muted = false);
public sealed record MediaTimelineOptions(int Width = 1920, int Height = 1080, int FramesPerSecond = 30,
    MediaEncoderPreference Encoder = MediaEncoderPreference.Auto, bool AllowSoftwareEncoding = false);
public sealed record MediaTimelineResult(string OutputPath, long SizeBytes, TimeSpan Elapsed, string EncoderName, bool IsHardware);
public sealed record MediaFileInfo(string Path, double DurationSeconds, bool HasAudio, int? Width, int? Height);

/// <summary>
/// Native trim, volume and timeline export. Hardware H.264 is probed with a real
/// frame on each supported vendor. Common scale/pad/fps and audio filters run on
/// CPU; there is no silent software video encoding fallback.
/// </summary>
public sealed partial class MediaExportService
{
    readonly SemaphoreSlim encoderProbeGate = new(1, 1);
    IReadOnlyList<MediaEncoderCapability>? encoderCapabilities;
    DateTimeOffset encoderProbeTime;

    public async Task<IReadOnlyList<MediaEncoderCapability>> ProbeEncodersAsync(CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        await encoderProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && encoderCapabilities is not null && DateTimeOffset.UtcNow - encoderProbeTime < TimeSpan.FromMinutes(5)) return encoderCapabilities;
            var results = new List<MediaEncoderCapability>();
            foreach (var preference in new[] { MediaEncoderPreference.NvidiaNvenc, MediaEncoderPreference.AmdAmf, MediaEncoderPreference.IntelQsv, MediaEncoderPreference.Cpu })
            {
                var (name, display, hardware) = EncoderIdentity(preference);
                if (!File.Exists(FfmpegPath)) { results.Add(new(preference, name, display, hardware, false, "FFmpeg не найден.")); continue; }
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
                    // QSV's implicit auto_any implementation can select a software
                    // runtime. Request MFX_IMPL_HARDWARE explicitly for both probe
                    // and export so it cannot masquerade as a hardware result.
                    if (preference == MediaEncoderPreference.IntelQsv) arguments.AddRange(new[] { "-init_hw_device", "qsv=flow:hw", "-filter_hw_device", "flow" });
                    arguments.AddRange(new[] { "-f", "lavfi", "-i", "color=c=black:s=128x128:r=1", "-frames:v", "1", "-vf", "format=nv12" });
                    arguments.AddRange(EncoderArguments(preference));
                    arguments.AddRange(new[] { "-f", "null", "-" });
                    var result = await RunAsync(arguments, null, deadline.Token).ConfigureAwait(false);
                    results.Add(new(preference, name, display, hardware, result.ExitCode == 0,
                        result.ExitCode == 0 ? display + " · проверено реальным кадром" : display + " недоступен: " + DescribeFailure(result.Error)));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { results.Add(new(preference, name, display, hardware, false, display + " не ответил за 15 секунд.")); }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { results.Add(new(preference, name, display, hardware, false, display + ": " + error.Message)); }
            }
            encoderCapabilities = results.AsReadOnly(); encoderProbeTime = DateTimeOffset.UtcNow;
            return encoderCapabilities;
        }
        finally { encoderProbeGate.Release(); }
    }

    public async Task<MediaFileInfo> InspectAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        var input = ValidateInput(inputPath);
        if (!File.Exists(FfmpegPath)) throw new FileNotFoundException("FFmpeg не найден.", FfmpegPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Read only the demuxer metadata, not the whole video. stderr is bounded by RunAsync.
        var result = await RunAsync(new[] { "-hide_banner", "-nostdin", "-protocol_whitelist", "file", "-i", input, "-t", "0", "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-f", "null", "-" }, null, deadline.Token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("Не удалось прочитать видеоклип. " + DescribeFailure(result.Error));
        var duration = Regex.Match(result.Error, @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant);
        if (!duration.Success) throw new InvalidOperationException("Длительность клипа не определена. Сохраните его в MP4 перед монтажом.");
        var seconds = double.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + double.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds) throw new InvalidOperationException("Клип имеет некорректную длительность.");
        var videoLine = result.Error.Split('\n').FirstOrDefault(line => line.Contains("Stream #", StringComparison.Ordinal) && line.Contains("Video:", StringComparison.Ordinal)) ?? "";
        var dimensions = Regex.Match(videoLine, @"\b(\d{2,5})x(\d{2,5})\b", RegexOptions.CultureInvariant);
        return new(input, seconds, Regex.IsMatch(result.Error, @"Stream #[^\r\n]*Audio:", RegexOptions.CultureInvariant),
            dimensions.Success ? int.Parse(dimensions.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            dimensions.Success ? int.Parse(dimensions.Groups[2].Value, CultureInfo.InvariantCulture) : null);
    }

    public async Task<MediaTimelineResult> ExportTimelineAsync(IReadOnlyList<MediaTimelineClip> clips, string outputPath,
        MediaTimelineOptions? options = null, IProgress<MediaExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clips);
        options ??= new();
        ValidateTimelineOptions(options);
        if (clips.Count is < 1 or > 1000) throw new ArgumentException("Добавьте от 1 до 1000 клипов в монтаж.", nameof(clips));
        var destination = ValidateLocalPath(outputPath);
        if (!Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Монтаж экспортируется в MP4.", nameof(outputPath));
        if (File.Exists(destination)) throw new IOException("Файл назначения уже существует. Выберите новое имя.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Папка экспорта не существует.");
        var validated = clips.Select(ValidateTimelineClip).ToArray();
        if (validated.Any(clip => clip.Path.Equals(destination, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Исходный клип нельзя заменить экспортом.");
        var encoder = SelectEncoder(await ProbeEncodersAsync(cancellationToken).ConfigureAwait(false), options);
        var plans = new List<(MediaTimelineClip Clip, MediaFileInfo Info, double Duration)>();
        foreach (var clip in validated)
        {
            var info = await InspectAsync(clip.Path, cancellationToken).ConfigureAwait(false);
            var end = clip.TrimEndSeconds ?? info.DurationSeconds;
            if (clip.TrimStartSeconds >= info.DurationSeconds || end > info.DurationSeconds + .05)
                throw new ArgumentException("Границы обрезки выходят за длительность клипа: " + Path.GetFileName(clip.Path));
            var duration = Math.Min(end, info.DurationSeconds) - clip.TrimStartSeconds;
            if (duration < 1d / options.FramesPerSecond) throw new ArgumentException("После обрезки в клипе должен оставаться хотя бы один кадр.");
            plans.Add((clip, info, duration));
        }
        var stage = Path.Combine(parent, ".flow-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var cleanup = new List<string>();
        var clock = Stopwatch.StartNew();
        double completedSeconds = 0;
        long completedFrames = 0;
        try
        {
            for (var index = 0; index < plans.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = plans[index];
                var segment = Path.Combine(stage, $"segment-{index:D5}.mp4"); cleanup.Add(segment);
                var arguments = BuildTimelineSegmentArguments(plan.Clip, plan.Info.HasAudio, plan.Duration, segment, options, encoder.Preference, NvidiaGpuIndex);
                var offset = completedSeconds; var frameOffset = completedFrames;
                var segmentProgress = new InlineProgress<MediaExportProgress>(value => progress?.Report(value with
                { EncodedTime = TimeSpan.FromSeconds(offset) + value.EncodedTime, Frames = frameOffset + value.Frames, IsComplete = false }));
                var result = await RunAsync(arguments, segmentProgress, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0) throw new InvalidOperationException($"Монтаж: клип {index + 1} не обработан ({encoder.DisplayName}). " + DescribeFailure(result.Error));
                if (!File.Exists(segment) || new FileInfo(segment).Length == 0) throw new IOException("FFmpeg не сохранил клип монтажа.");
                completedSeconds += plan.Duration; completedFrames += (long)Math.Round(plan.Duration * options.FramesPerSecond);
            }
            var manifest = Path.Combine(stage, "timeline.ffconcat"); cleanup.Add(manifest);
            await File.WriteAllLinesAsync(manifest, new[] { "ffconcat version 1.0" }.Concat(Enumerable.Range(0, plans.Count).Select(index => $"file 'segment-{index:D5}.mp4'")), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var temporary = Path.Combine(stage, "result.mp4"); cleanup.Add(temporary);
            var joined = await RunAsync(new[] { "-hide_banner", "-loglevel", "warning", "-nostdin", "-n", "-protocol_whitelist", "file", "-f", "concat", "-safe", "1", "-i", manifest,
                "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-movflags", "+faststart", temporary }, null, cancellationToken).ConfigureAwait(false);
            if (joined.ExitCode != 0) throw new InvalidOperationException("Не удалось склеить клипы монтажа. " + DescribeFailure(joined.Error));
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("FFmpeg не создал результат монтажа.");
            File.Move(temporary, destination, overwrite: false);
            clock.Stop();
            progress?.Report(new(TimeSpan.FromSeconds(completedSeconds), completedFrames, "", true));
            return new(destination, new FileInfo(destination).Length, clock.Elapsed, encoder.EncoderName, encoder.IsHardware);
        }
        finally
        {
            // Driver teardown / antivirus can briefly hold a just-closed file.
            // Retry only this operation's known files and exact empty directory.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                foreach (var file in cleanup) DeleteTemporaryFile(file);
                try { Directory.Delete(stage, recursive: false); break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { if (attempt < 3) await Task.Delay(100, CancellationToken.None).ConfigureAwait(false); }
            }
        }
    }

    internal static MediaEncoderCapability SelectEncoder(IReadOnlyList<MediaEncoderCapability> capabilities, MediaTimelineOptions options)
    {
        if (options.Encoder == MediaEncoderPreference.Cpu && !options.AllowSoftwareEncoding) throw new InvalidOperationException("Для CPU-кодирования включите явное разрешение программного экспорта.");
        var selected = options.Encoder == MediaEncoderPreference.Auto ? capabilities.FirstOrDefault(value => value.IsHardware && value.Available)
            : capabilities.FirstOrDefault(value => value.Preference == options.Encoder && value.Available);
        return selected ?? throw new InvalidOperationException(options.Encoder == MediaEncoderPreference.Auto
            ? "Аппаратное H.264-кодирование недоступно. Проверьте NVENC, AMD AMF или Intel Quick Sync в настройках. CPU автоматически не используется."
            : "Выбранный кодировщик недоступен. Выполните проверку GPU в настройках.");
    }

    internal static IReadOnlyList<string> BuildTimelineSegmentArguments(MediaTimelineClip clip, bool hasAudio, double duration, string destination, MediaTimelineOptions options, MediaEncoderPreference encoder, int nvidiaGpuIndex = 0)
    {
        ValidateTimelineOptions(options);
        var seconds = duration.ToString("0.########", CultureInfo.InvariantCulture);
        var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin", "-n" };
        if (encoder == MediaEncoderPreference.IntelQsv) arguments.AddRange(new[] { "-init_hw_device", "qsv=flow:hw", "-filter_hw_device", "flow" });
        arguments.AddRange(new[] { "-protocol_whitelist", "file", "-ss", clip.TrimStartSeconds.ToString("0.########", CultureInfo.InvariantCulture), "-i", clip.Path });
        if (!hasAudio) arguments.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000" });
        arguments.AddRange(new[] { "-t", seconds, "-map", "0:v:0", "-map", hasAudio ? "0:a:0" : "1:a:0", "-vf",
            $"scale={options.Width}:{options.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={options.Width}:{options.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={options.FramesPerSecond},format=nv12",
            "-af", $"aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={(clip.Muted ? 0 : clip.Volume).ToString("0.########", CultureInfo.InvariantCulture)},apad",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2" });
        arguments.AddRange(EncoderArguments(encoder, nvidiaGpuIndex));
        arguments.AddRange(new[] { "-profile:v", "high", "-pix_fmt", "nv12", "-video_track_timescale", "90000", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", destination });
        return arguments;
    }

    static MediaTimelineClip ValidateTimelineClip(MediaTimelineClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(clip.TrimStartSeconds) || clip.TrimStartSeconds < 0 || clip.TrimEndSeconds is { } end && (!double.IsFinite(end) || end <= clip.TrimStartSeconds)
            || !double.IsFinite(clip.Volume) || clip.Volume < 0 || clip.Volume > 4) throw new ArgumentException("Проверьте обрезку и громкость клипа (0–400%).");
        return clip with { Path = ValidateInput(clip.Path) };
    }

    static void ValidateTimelineOptions(MediaTimelineOptions options)
    {
        if (options.Width is < 64 or > 7680 || options.Height is < 64 or > 4320 || options.Width % 2 != 0 || options.Height % 2 != 0 || options.FramesPerSecond is < 1 or > 120 || !Enum.IsDefined(options.Encoder))
            throw new ArgumentException("Выберите чётное разрешение от 64×64 до 7680×4320 и частоту от 1 до 120 кадров/с.");
    }

    static (string Name, string Display, bool Hardware) EncoderIdentity(MediaEncoderPreference preference) => preference switch
    {
        MediaEncoderPreference.NvidiaNvenc => ("h264_nvenc", "NVIDIA NVENC", true),
        MediaEncoderPreference.AmdAmf => ("h264_amf", "AMD AMF", true),
        MediaEncoderPreference.IntelQsv => ("h264_qsv", "Intel Quick Sync", true),
        MediaEncoderPreference.Cpu => ("libx264", "CPU · H.264", false),
        _ => throw new ArgumentException("Выберите конкретный кодировщик.")
    };

    IReadOnlyList<string> EncoderArguments(MediaEncoderPreference preference) => EncoderArguments(preference, NvidiaGpuIndex);
    static IReadOnlyList<string> EncoderArguments(MediaEncoderPreference preference, int nvidiaGpuIndex) => preference switch
    {
        MediaEncoderPreference.NvidiaNvenc => new[] { "-c:v", "h264_nvenc", "-gpu", nvidiaGpuIndex.ToString(CultureInfo.InvariantCulture), "-preset", "p5", "-rc", "vbr", "-cq", "21", "-b:v", "0" },
        MediaEncoderPreference.AmdAmf => new[] { "-c:v", "h264_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", "21", "-qp_p", "23" },
        MediaEncoderPreference.IntelQsv => new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "22" },
        MediaEncoderPreference.Cpu => new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "20" },
        _ => throw new ArgumentException("Выберите конкретный кодировщик.")
    };

    sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
