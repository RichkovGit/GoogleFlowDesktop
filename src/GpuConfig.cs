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

        public HardwareSpecs()
        {
            DgpuName = "NVIDIA GeForce GTX 1650";
            DgpuVram = "4.0 GB";
            DgpuDriver = "";
            IgpuName = "Intel(R) UHD Graphics";
            IgpuVram = "2.0 GB";
            CpuName = "Intel Core i5";
            CpuCores = 6;
            CpuThreads = 12;
            TotalRamGb = 16.0;
            FreeRamGb = 8.0;
            AppRamMb = 25.0;
        }
    }

    public static class GpuConfig
    {
        [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", ExactSpelling = true, CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int SetProcessWorkingSetSize(IntPtr process, int minimumWorkingSetSize, int maximumWorkingSetSize);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        private static HardwareSpecs cachedSpecs = null;

        public static HardwareSpecs DetectHardware()
        {
            if (cachedSpecs != null)
            {
                cachedSpecs.AppRamMb = GetCurrentMemoryUsageMb();
                return cachedSpecs;
            }

            var specs = new HardwareSpecs();

            // 1. Detect GPUs (Video Controllers)
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string name = (mo["Name"] as string) ?? "";
                        ulong ramBytes = 0;
                        try { ramBytes = Convert.ToUInt64(mo["AdapterRAM"]); } catch { }
                        string driver = (mo["DriverVersion"] as string) ?? "";
                        double vramGb = Math.Round(ramBytes / (1024.0 * 1024.0 * 1024.0), 1);

                        if (name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Radeon RX", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Arc", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            specs.DgpuName = name;
                            if (vramGb > 0) specs.DgpuVram = vramGb.ToString("0.0") + " GB VRAM";
                            specs.DgpuDriver = driver;
                        }
                        else if (name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("UHD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("Iris", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            specs.IgpuName = name;
                            if (vramGb > 0) specs.IgpuVram = vramGb.ToString("0.0") + " GB VRAM";
                        }
                    }
                }
            }
            catch { }

            // 2. Detect CPU (Processor)
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        specs.CpuName = ((mo["Name"] as string) ?? "Intel Processor").Trim();
                        try { specs.CpuCores = Convert.ToInt32(mo["NumberOfCores"]); } catch { }
                        try { specs.CpuThreads = Convert.ToInt32(mo["NumberOfLogicalProcessors"]); } catch { }
                        break;
                    }
                }
            }
            catch { }

            // 3. Detect Total System RAM
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
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

        public static string GetPrimaryGpuName()
        {
            return DetectHardware().DgpuName;
        }

        public static bool EnforceWindowsDirectXGpuPreference()
        {
            bool success = false;
            try
            {
                string keyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    if (key != null)
                    {
                        string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                        key.SetValue(currentExe, "GpuPreference=2;");
                        success = true;

                        string webviewDir = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application";
                        if (Directory.Exists(webviewDir))
                        {
                            foreach (string exe in Directory.GetFiles(webviewDir, "msedgewebview2.exe", SearchOption.AllDirectories))
                            {
                                try
                                {
                                    key.SetValue(exe, "GpuPreference=2;");
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }
            return success;
        }

        public static string GetChromiumDgpuArguments()
        {
            return " " + string.Join(" ", new string[] {
                "--force_high_performance_gpu",
                "--gpu-preference=2",
                "--enable-gpu-rasterization",
                "--enable-zero-copy",
                "--ignore-gpu-blocklist",
                "--enable-features=VaapiVideoDecoder,D3D11VideoDecoder,PlatformHEVCDecoderSupport",
                "--enable-accelerated-video-decode",
                "--enable-accelerated-mjpeg-decode"
            });
        }

        public static void TrimProcessMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
            }
            catch { }
        }

        public static double GetCurrentMemoryUsageMb()
        {
            try
            {
                using (var proc = Process.GetCurrentProcess())
                {
                    return Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1);
                }
            }
            catch
            {
                return 25.0;
            }
        }
    }
}
