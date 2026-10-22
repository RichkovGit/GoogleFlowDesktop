using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FlowStudio.Services;

public static class MediaEditingSelfTest
{
    public static async Task<string> RunAsync(string qaDirectory, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetFullPath(qaDirectory), "timeline"); Directory.CreateDirectory(directory);
        var available = new[]
        {
            new MediaEncoderCapability(MediaEncoderPreference.NvidiaNvenc, "h264_nvenc", "NVIDIA NVENC", true, false, "test"),
            new MediaEncoderCapability(MediaEncoderPreference.AmdAmf, "h264_amf", "AMD AMF", true, true, "test"),
            new MediaEncoderCapability(MediaEncoderPreference.IntelQsv, "h264_qsv", "Intel Quick Sync", true, true, "test"),
            new MediaEncoderCapability(MediaEncoderPreference.Cpu, "libx264", "CPU", false, true, "test")
        };
        Require(MediaExportService.SelectEncoder(available, new()).Preference == MediaEncoderPreference.AmdAmf, "Auto should choose an available hardware encoder.");
        RequireThrows(() => MediaExportService.SelectEncoder(available.Where(value => !value.IsHardware).ToArray(), new()), "Auto must never fall back to CPU.");
        RequireThrows(() => MediaExportService.SelectEncoder(available, new(Encoder: MediaEncoderPreference.Cpu)), "CPU needs explicit permission.");
        Require(!MediaExportService.SelectEncoder(available, new(Encoder: MediaEncoderPreference.Cpu, AllowSoftwareEncoding: true)).IsHardware, "Explicit CPU should remain labelled CPU.");
        var syntheticClip = new MediaTimelineClip(@"C:\video's\клип.mp4", .25, 1.25, .5);
        var amdArgs = MediaExportService.BuildTimelineSegmentArguments(syntheticClip, false, 1, @"C:\result.mp4", new(320, 180, 24), MediaEncoderPreference.AmdAmf);
        var intelArgs = MediaExportService.BuildTimelineSegmentArguments(syntheticClip with { Muted = true }, true, 1, @"C:\result.mp4", new(320, 180, 24), MediaEncoderPreference.IntelQsv);
        Require(amdArgs.Contains("h264_amf") && !amdArgs.Contains("h264_nvenc") && !amdArgs.Contains("libx264") && amdArgs.Contains("anullsrc=channel_layout=stereo:sample_rate=48000"), "AMD arguments or silent audio normalization are incorrect.");
        Require(intelArgs.Contains("h264_qsv") && intelArgs.Contains("qsv=flow:hw") && !intelArgs.Contains("h264_nvenc") && intelArgs.Any(value => value.Contains("volume=0,", StringComparison.Ordinal)), "Intel encoder must explicitly request hardware; mute arguments are incorrect.");
        Require(amdArgs.Contains(syntheticClip.Path) && amdArgs.Contains("0.25") && amdArgs.Any(value => value.Contains("scale=320:180", StringComparison.Ordinal)), "Paths, trim decimals and target size must be passed as separate invariant arguments.");

        var exporter = new MediaExportService();
        var capabilities = await exporter.ProbeEncodersAsync(cancellationToken).ConfigureAwait(false);
        var chosen = capabilities.FirstOrDefault(value => value.IsHardware && value.Available) ?? capabilities.FirstOrDefault(value => value.Preference == MediaEncoderPreference.Cpu && value.Available)
            ?? throw new InvalidOperationException("No working test encoder: " + string.Join("; ", capabilities.Select(value => value.Message)));
        var first = Path.Combine(directory, "первый 'со звуком'.mp4");
        var second = Path.Combine(directory, "second-no-audio.mp4");
        // Fixtures are local synthetic data; explicit software fixture generation
        // is unrelated to the app's hardware-only default export setting.
        await RunToolAsync(exporter.FfmpegPath, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "color=c=red:s=192x108:r=24:d=2", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", first }, cancellationToken).ConfigureAwait(false);
        await RunToolAsync(exporter.FfmpegPath, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "color=c=blue:s=256x144:r=30:d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", second }, cancellationToken).ConfigureAwait(false);
        var firstInfo = await exporter.InspectAsync(first, cancellationToken).ConfigureAwait(false);
        var secondInfo = await exporter.InspectAsync(second, cancellationToken).ConfigureAwait(false);
        Require(firstInfo.HasAudio && !secondInfo.HasAudio && firstInfo.Width == 192 && firstInfo.Height == 108, "Source stream inspection is incorrect.");
        var output = Path.Combine(directory, "timeline-trimmed.mp4");
        var options = new MediaTimelineOptions(320, 180, 24, chosen.Preference, AllowSoftwareEncoding: !chosen.IsHardware);
        var exported = await exporter.ExportTimelineAsync(new[] { new MediaTimelineClip(first, .25, 1.25, .5), new MediaTimelineClip(second, .5, 1.25, Muted: true) }, output, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var finalInfo = await exporter.InspectAsync(output, cancellationToken).ConfigureAwait(false);
        Require(finalInfo.Width == 320 && finalInfo.Height == 180 && finalInfo.HasAudio && Math.Abs(finalInfo.DurationSeconds - 1.75) < .12, "Timeline trim, normalized dimensions/audio or duration are incorrect.");
        var decoded = await RunToolAsync(exporter.FfmpegPath, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", output, "-map", "0:v:0", "-an", "-f", "null", "-", "-progress", "pipe:1" }, cancellationToken).ConfigureAwait(false);
        var frameLine = decoded.Split('\n').LastOrDefault(line => line.StartsWith("frame=", StringComparison.Ordinal));
        Require(frameLine is not null && int.TryParse(frameLine.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) && frames == 42, "Independent decode must return 42 trimmed video frames.");
        Require(exported.EncoderName == chosen.EncoderName && exported.IsHardware == chosen.IsHardware && exported.SizeBytes > 0, "Export result encoder identity is incorrect.");
        var refused = false;
        try { await exporter.ExportTimelineAsync(new[] { new MediaTimelineClip(first) }, output, options, cancellationToken: cancellationToken).ConfigureAwait(false); } catch (IOException) { refused = true; }
        Require(refused, "Timeline output must not overwrite existing files.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var canceledOutput = Path.Combine(directory, "canceled.mp4");
        var canceled = false;
        try
        {
            await exporter.ExportTimelineAsync(new[] { new MediaTimelineClip(first), new MediaTimelineClip(second) }, canceledOutput, options,
                new CancelProgress(cancel), cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { canceled = true; }
        Require(canceled && !File.Exists(canceledOutput) && !Directory.EnumerateDirectories(directory, ".flow-edit-*").Any(), "Timeline cancel must remove all staged files and leave no output.");
        return $"AMF/QSV argument contracts and explicit CPU policy passed. Real {chosen.EncoderName} trim/volume/normalize/concat: 42 decoded frames, {finalInfo.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s, 320×180 stereo; mixed silent/audio sources, overwrite protection and cancel cleanup passed. Other vendors were probed, not assumed available.";
    }

    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static void RequireThrows(Action action, string message) { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException(message); }
    sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<MediaExportProgress> { public void Report(MediaExportProgress value) { if (value.Frames > 0) cancellation.Cancel(); } }
    static async Task<string> RunToolAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start fixture verification.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); await Task.WhenAll(output, error).ConfigureAwait(false); throw; }
        await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Fixture verification: " + await error.ConfigureAwait(false));
        return await output.ConfigureAwait(false);
    }
}
