using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace FlowStudio.Services;

public sealed record GraphicsAdapterInfo(
    string Name,
    string Vendor,
    string DriverVersion,
    ulong? DedicatedMemoryBytes,
    bool IsNvidia)
{
    public bool IsAmd => Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || Vendor.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase) || Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
    public bool IsIntel => Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);
    // CIM does not report reliable shared/dedicated architecture for every driver.
    // Use a conservative name heuristic only for ordering, never as a capability probe.
    public bool IsLikelyDiscrete => IsNvidia || (IsAmd && (Name.Contains(" RX ", StringComparison.OrdinalIgnoreCase) || Name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase) || Name.Contains("FirePro", StringComparison.OrdinalIgnoreCase)))
        || (IsIntel && System.Text.RegularExpressions.Regex.IsMatch(Name, @"Arc.*(?:[AB][357]\d{2}|Pro)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
    public string HardwareEncoderFamily => IsNvidia ? "NVENC" : IsAmd ? "AMF" : IsIntel ? "Quick Sync" : "Проверяется FFmpeg";
}

public sealed record NvidiaGpuInfo(int Index, string Name, long MemoryTotalMb, int? UtilizationPercent);

public sealed record GpuPreferenceResult(bool Success, bool RestartRequired, string Message);

public sealed record HardwareSnapshot(
    IReadOnlyList<GraphicsAdapterInfo> Adapters,
    IReadOnlyList<NvidiaGpuInfo> NvidiaGpus,
    int RenderTier,
    bool HighPerformancePreferenceEnabled,
    string? Diagnostic)
{
    public bool HasNvidia => NvidiaGpus.Count > 0 || Adapters.Any(adapter => adapter.IsNvidia);
    public bool HasAmd => Adapters.Any(adapter => adapter.IsAmd);
    public bool HasIntel => Adapters.Any(adapter => adapter.IsIntel);
    public string PreferredGpuName => NvidiaGpus.FirstOrDefault()?.Name
        ?? Adapters.OrderByDescending(adapter => adapter.IsLikelyDiscrete).ThenByDescending(adapter => adapter.DedicatedMemoryBytes ?? 0).FirstOrDefault()?.Name
        ?? "Видеокарта не определена";
    public string RenderingDescription => RenderTier switch
    {
        2 => "WPF · аппаратное ускорение, уровень 2",
        1 => "WPF · частичное аппаратное ускорение",
        _ => "WPF · программный рендеринг"
    };
}

/// <summary>
/// Reports adapter capabilities. A Windows GPU preference is a request to the OS;
/// it is not proof that WPF or a separate video decoder uses a particular adapter.
/// </summary>
public sealed class HardwareService
{
    private const string GpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <param name="renderTier">Pass System.Windows.Media.RenderCapability.Tier &gt;&gt; 16 from the UI thread.</param>
    public async Task<HardwareSnapshot> GetSnapshotAsync(int renderTier, CancellationToken cancellationToken = default)
    {
        var adapterTask = ReadAdaptersAsync(cancellationToken);
        var nvidiaTask = ReadNvidiaGpusAsync(cancellationToken);
        await Task.WhenAll(adapterTask, nvidiaTask).ConfigureAwait(false);
        var (adapters, adapterDiagnostic) = await adapterTask.ConfigureAwait(false);
        var (nvidiaGpus, nvidiaDiagnostic) = await nvidiaTask.ConfigureAwait(false);

        // Win32_VideoController.AdapterRAM is a 32-bit value and can underreport
        // modern cards. Prefer the driver's 64-bit memory report when available.
        var availableNvidia = new List<NvidiaGpuInfo>(nvidiaGpus);
        var enriched = adapters.Select(adapter =>
        {
            var match = availableNvidia.FirstOrDefault(gpu =>
                string.Equals(gpu.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return adapter;
            availableNvidia.Remove(match);
            return adapter with { DedicatedMemoryBytes = checked((ulong)match.MemoryTotalMb * 1024UL * 1024UL) };
        }).ToArray();

        var diagnostic = string.Join(" ", new[] { adapterDiagnostic, nvidiaDiagnostic }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new HardwareSnapshot(enriched, nvidiaGpus, Math.Clamp(renderTier, 0, 2),
            HasHighPerformancePreference(), string.IsNullOrWhiteSpace(diagnostic) ? null : diagnostic);
    }

    public GpuPreferenceResult PreferHighPerformanceGpuForCurrentExecutable()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return new(false, false, "Не удалось определить путь приложения.");

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesKey, writable: true);
            if (key is null)
                return new(false, false, "Windows не разрешила сохранить предпочтение видеокарты.");
            var oldValue = key.GetValue(executablePath) as string ?? string.Empty;
            // Preserve options that Windows or the user added for this executable.
            var settings = oldValue.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.TrimStart().StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                .ToList();
            settings.Add("GpuPreference=2");
            var value = string.Join(';', settings) + ";";
            var changed = !string.Equals(oldValue, value, StringComparison.Ordinal);
            if (changed)
                key.SetValue(executablePath, value, RegistryValueKind.String);
            return new(true, changed, changed
                ? "Для приложения выбран высокопроизводительный GPU. Перезапустите приложение; окончательный выбор делает Windows."
                : "Предпочтение высокопроизводительного GPU уже включено. Окончательный выбор делает Windows.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new(false, false, $"Не удалось сохранить предпочтение GPU: {exception.Message}");
        }
    }

    public bool HasHighPerformancePreference()
    {
        try
        {
            if (Environment.ProcessPath is not { } executablePath)
                return false;
            using var key = Registry.CurrentUser.OpenSubKey(GpuPreferencesKey);
            return (key?.GetValue(executablePath) as string ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => string.Equals(part.Trim(), "GpuPreference=2", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<NvidiaGpuInfo>> GetNvidiaGpusAsync(CancellationToken cancellationToken = default)
        => (await ReadNvidiaGpusAsync(cancellationToken).ConfigureAwait(false)).Gpus;

    private static async Task<(IReadOnlyList<GraphicsAdapterInfo> Adapters, string? Diagnostic)> ReadAdaptersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var result = await RunAsync(powershell, new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; @(Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object Name,AdapterCompatibility,DriverVersion,AdapterRAM) | ConvertTo-Json -Compress"
            }, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return (Array.Empty<GraphicsAdapterInfo>(), "Windows не вернула список графических адаптеров.");
            if (string.IsNullOrWhiteSpace(result.Output))
                return (Array.Empty<GraphicsAdapterInfo>(), "Графические адаптеры не найдены.");
            using var json = JsonDocument.Parse(result.Output.TrimStart('\uFEFF'));
            var rows = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray().ToArray()
                : new[] { json.RootElement };
            var adapters = new List<GraphicsAdapterInfo>();
            foreach (var row in rows)
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                var name = GetString(row, "Name", "Неизвестный адаптер");
                var vendor = GetString(row, "AdapterCompatibility", string.Empty);
                var driver = GetString(row, "DriverVersion", string.Empty);
                ulong? memory = row.TryGetProperty("AdapterRAM", out var ram)
                    && ram.ValueKind == JsonValueKind.Number
                    && ram.TryGetUInt64(out var bytes) && bytes > 0 ? bytes : null;
                adapters.Add(new(name, vendor, driver, memory,
                    name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    || vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)));
            }
            return (adapters, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
        {
            return (Array.Empty<GraphicsAdapterInfo>(), "Обнаружение видеокарт Windows временно недоступно.");
        }
    }

    private static async Task<(IReadOnlyList<NvidiaGpuInfo> Gpus, string? Diagnostic)> ReadNvidiaGpusAsync(CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null)
            return (Array.Empty<NvidiaGpuInfo>(), null);
        try
        {
            var result = await RunAsync(executable, new[]
            {
                "--query-gpu=index,name,memory.total,utilization.gpu", "--format=csv,noheader,nounits"
            }, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return (Array.Empty<NvidiaGpuInfo>(), "NVIDIA-SMI не вернула данные драйвера.");
            var devices = new List<NvidiaGpuInfo>();
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',').Select(part => part.Trim()).ToArray();
                if (parts.Length < 4 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    continue;
                var name = string.Join(", ", parts.Skip(1).Take(parts.Length - 3));
                _ = long.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var memory);
                int? utilization = int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
                    ? Math.Clamp(percent, 0, 100) : null;
                devices.Add(new(index, name, Math.Max(0, memory), utilization));
            }
            return (devices, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return (Array.Empty<NvidiaGpuInfo>(), "Телеметрия NVIDIA временно недоступна.");
        }
    }

    private static string GetString(JsonElement element, string property, string fallback)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить диагностику GPU.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return (process.ExitCode, await outputTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
