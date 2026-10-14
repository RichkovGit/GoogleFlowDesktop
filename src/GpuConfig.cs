using System;
using System.IO;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Microsoft.Win32;

namespace GoogleFlowDesktop
{
    public class HardwareSpecs
    {
        public string DgpuName { get; set; }
        public string DgpuVram { get; set; }
        public string DgpuDriver { get; set; }
        public string IgpuName { get; set; }
        public string IgpuVram { get; set; }
        public string CpuName { get; set; }
        public int CpuCores { get; set; }
        public int CpuThreads { get; set; }
        public double TotalRamGb { get; set; }
        public double FreeRamGb { get; set; }
        public double AppRamMb { get; set; }
        public bool HasDgpu { get; set; }

        public HardwareSpecs()
        {
            DgpuName = "Дискретная видеокарта (dGPU)";
            DgpuVram = "выделенная VRAM";
            DgpuDriver = "";
            IgpuName = "Интегрированная графика (iGPU)";
            IgpuVram = "общая память";
            CpuName = "Процессор";
            CpuCores = 4;
            CpuThreads = 8;
            TotalRamGb = 16.0;
            FreeRamGb = 8.0;
            AppRamMb = 30.0;
            HasDgpu = false;
        }
    }

    public static class GpuConfig
    {
        [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", ExactSpelling = true, CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int SetProcessWorkingSetSize(IntPtr process, int minimumWorkingSetSize, int maximumWorkingSetSize);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        private static HardwareSpecs cachedSpecs;

        public static HardwareSpecs DetectHardware()
        {
            if (cachedSpecs != null)
            {
                cachedSpecs.AppRamMb = GetCurrentMemoryUsageMb();
                return cachedSpecs;
            }

            HardwareSpecs specs = new HardwareSpecs();
            Dictionary<string, ulong> vramByName = ReadVramFromRegistry();

            // ---- 1. Video controllers ----
            try
            {
                using (ManagementObjectSearcher searcher =
                    new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string name = (mo["Name"] as string) ?? "";
                        if (name.Length == 0) continue;

                        string driver = (mo["DriverVersion"] as string) ?? "";
                        ulong ramBytes = 0;
                        if (vramByName.ContainsKey(name)) ramBytes = vramByName[name];
                        if (ramBytes == 0)
                        {
                            try { ramBytes = Convert.ToUInt64(mo["AdapterRAM"]); } catch { }
                        }
                        string vram = FormatVram(ramBytes);

                        if (IsDiscrete(name))
                        {
                            specs.DgpuName = name;
                            specs.DgpuVram = vram;
                            specs.DgpuDriver = driver;
                            specs.HasDgpu = true;
                        }
                        else if (IsIntegrated(name))
                        {
                            specs.IgpuName = name;
                            specs.IgpuVram = vram;
                        }
                    }
                }
            }
            catch { }

            // ---- 2. CPU ----
            try
            {
                using (ManagementObjectSearcher searcher =
                    new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        specs.CpuName = ((mo["Name"] as string) ?? "Процессор").Trim();
                        try { specs.CpuCores = Convert.ToInt32(mo["NumberOfCores"]); } catch { }
                        try { specs.CpuThreads = Convert.ToInt32(mo["NumberOfLogicalProcessors"]); } catch { }
                        break;
                    }
                }
            }
            catch { }

            // ---- 3. RAM ----
            try
            {
                using (ManagementObjectSearcher searcher =
                    new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        ulong totalKb = Convert.ToUInt64(mo["TotalVisibleMemorySize"]);
                        ulong freeKb = Convert.ToUInt64(mo["FreePhysicalMemory"]);
                        specs.TotalRamGb = Math.Round(totalKb / (1024.0 * 1024.0), 1);
                        specs.FreeRamGb = Math.Round(freeKb / (1024.0 * 1024.0), 1);
                        break;
                    }
                }
            }
            catch { }

            specs.AppRamMb = GetCurrentMemoryUsageMb();
            cachedSpecs = specs;
            return specs;
        }

        private static bool IsDiscrete(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx")) return true;
            if (n.Contains("radeon rx") || n.Contains("radeon pro") || n.Contains("firepro")) return true;
            if (n.Contains("arc a") || n.Contains("arc b") || n.Contains("intel arc")) return true;
            if (n.Contains("microsoft basic")) return false;
            return false;
        }

        private static bool IsIntegrated(string name)
        {
            string n = name.ToLowerInvariant();
            if (IsDiscrete(name)) return false;
            if (n.Contains("intel") || n.Contains("uhd") || n.Contains("iris")) return true;
            if (n.Contains("radeon") || n.Contains("vega")) return true;
            return false;
        }

        private static string FormatVram(ulong bytes)
        {
            if (bytes < 1024UL * 1024UL) return "выделенная VRAM";
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            if (gb >= 1.0) return gb.ToString("0.0") + " GB VRAM";
            return Math.Round(bytes / (1024.0 * 1024.0), 0) + " MB VRAM";
        }

        /// <summary>
        /// WMI's AdapterRAM is a 32-bit field and is unreliable above 4 GB.
        /// The accurate value lives in the display class registry key as a QWORD.
        /// </summary>
        private static Dictionary<string, ulong> ReadVramFromRegistry()
        {
            Dictionary<string, ulong> result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string classPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using (RegistryKey baseKey = Registry.LocalMachine.OpenSubKey(classPath))
                {
                    if (baseKey == null) return result;
                    foreach (string sub in baseKey.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue; // 0000, 0001, ...
                        using (RegistryKey k = baseKey.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            string desc = k.GetValue("DriverDesc") as string;
                            if (string.IsNullOrEmpty(desc)) continue;

                            ulong vram = 0;
                            object qw = k.GetValue("HardwareInformation.qwMemorySize");
                            if (qw != null)
                            {
                                try { vram = Convert.ToUInt64(qw); } catch { }
                            }
                            if (vram == 0)
                            {
                                object dw = k.GetValue("HardwareInformation.MemorySize");
                                if (dw != null)
                                {
                                    try { vram = Convert.ToUInt64(dw); } catch { }
                                }
                            }
                            if (vram > 0 && !result.ContainsKey(desc)) result[desc] = vram;
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        public static string GetPrimaryGpuName()
        {
            return DetectHardware().DgpuName;
        }

        /// <summary>Writes GpuPreference=2 (High performance) for this app and the WebView2 runtime.</summary>
        public static bool EnforceWindowsDirectXGpuPreference()
        {
            bool success = false;
            try
            {
                string keyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    if (key == null) return false;

                    string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                    key.SetValue(currentExe, "GpuPreference=2;");
                    success = true;

                    // WebView2 runtime that is actually in use on this machine.
                    string runtimeExe = GetWebView2RuntimeExe();
                    if (!string.IsNullOrEmpty(runtimeExe))
                    {
                        try { key.SetValue(runtimeExe, "GpuPreference=2;"); } catch { }
                    }
                }
            }
            catch { }
            return success;
        }

        /// <summary>Locates msedgewebview2.exe without scanning the whole disk.</summary>
        public static string GetWebView2RuntimeExe()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] roots = new string[]
                {
                    Path.Combine(local, "Microsoft", "EdgeWebView", "Application"),
                    @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application",
                    @"C:\Program Files\Microsoft\EdgeWebView\Application"
                };
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string exe = Path.Combine(dir, "msedgewebview2.exe");
                        if (!File.Exists(exe)) continue;
                        DateTime t = File.GetLastWriteTimeUtc(exe);
                        if (best == null || t > bestTime) { best = exe; bestTime = t; }
                    }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>Chromium/WebView2 switches for discrete-GPU acceleration.</summary>
        public static string GetChromiumDgpuArguments()
        {
            string[] args = new string[]
            {
                "--force_high_performance_gpu",
                "--enable-gpu-rasterization",
                "--enable-zero-copy",
                "--ignore-gpu-blocklist",
                "--enable-accelerated-video-decode",
                "--enable-accelerated-mjpeg-decode",
                "--enable-features=PlatformHEVCDecoderSupport,CanvasOopRasterization",
                "--use-angle=d3d11"
            };
            return " " + string.Join(" ", args);
        }

        /// <summary>Explicit user-requested working-set trim (never called on a timer).</summary>
        public static void TrimProcessMemory()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Optimized);
                GC.WaitForPendingFinalizers();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
            }
            catch { }
        }

        public static double GetCurrentMemoryUsageMb()
        {
            try
            {
                using (Process proc = Process.GetCurrentProcess())
                {
                    return Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1);
                }
            }
            catch
            {
                return 30.0;
            }
        }
    }
}
