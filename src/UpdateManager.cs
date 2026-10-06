using System;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string DownloadUrl { get; set; }
        public string ReleaseDate { get; set; }
        public List<string> ReleaseNotes { get; set; }
        public string Error { get; set; }

        public UpdateInfo()
        {
            ReleaseNotes = new List<string>();
        }
    }

    public static class UpdateManager
    {
        public const string CurrentVersion = "1.0.0";
        private const string VersionCheckUrl = "https://raw.githubusercontent.com/RichkovGit/GoogleFlowDesktop/main/version.json";
        private static readonly HttpClient Client = new HttpClient();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static UpdateManager()
        {
            Client.Timeout = TimeSpan.FromSeconds(6);
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("GoogleFlowDesktop/" + CurrentVersion);
        }

        public static async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            var result = new UpdateInfo
            {
                CurrentVersion = CurrentVersion,
                HasUpdate = false
            };

            try
            {
                string jsonString = await Client.GetStringAsync(VersionCheckUrl);
                if (string.IsNullOrEmpty(jsonString))
                {
                    result.Error = "Пустой ответ от сервера обновлений";
                    return result;
                }

                var dict = Json.Deserialize<Dictionary<string, object>>(jsonString);
                if (dict != null && dict.ContainsKey("version"))
                {
                    string latestVer = dict["version"].ToString().Trim();
                    result.LatestVersion = latestVer;

                    if (dict.ContainsKey("download_url"))
                    {
                        result.DownloadUrl = dict["download_url"].ToString();
                    }

                    if (dict.ContainsKey("release_date"))
                    {
                        result.ReleaseDate = dict["release_date"].ToString();
                    }

                    if (dict.ContainsKey("release_notes"))
                    {
                        var notesArr = dict["release_notes"] as System.Collections.ArrayList;
                        if (notesArr != null)
                        {
                            foreach (var n in notesArr)
                            {
                                if (n != null) result.ReleaseNotes.Add(n.ToString());
                            }
                        }
                    }

                    Version cur = ParseVersion(CurrentVersion);
                    Version lat = ParseVersion(latestVer);

                    result.HasUpdate = (lat > cur);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        public static async Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, Action<int> progressCallback = null)
        {
            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string tempNewExe = Path.Combine(appDir, "GoogleFlowDesktop_update.tmp");
                string updaterBat = Path.Combine(appDir, "updater.bat");

                // Download new binary directly with streaming
                using (var response = await Client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long totalBytes = response.Content.Headers.ContentLength ?? -1L;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fs = new FileStream(tempNewExe, FileMode.Create, FileAccess.Write, FileShare.None, 8192))
                    {
                        byte[] buffer = new byte[8192];
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;

                            if (totalBytes > 0 && progressCallback != null)
                            {
                                int pct = (int)((totalRead * 100) / totalBytes);
                                progressCallback(pct);
                            }
                        }
                    }
                }

                if (!File.Exists(tempNewExe) || new FileInfo(tempNewExe).Length < 10000)
                {
                    throw new Exception("Загруженный файл поврежден или пуст");
                }

                int currentPid = Process.GetCurrentProcess().Id;

                // Create standalone updater batch script
                string batScript = string.Format(@"@echo off
setlocal
chcp 65001 >nul
title Google Flow Desktop Updater
echo Ожидание завершения процесса #{0}...
:wait_loop
tasklist /fi ""PID eq {0}"" 2>nul | find ""{0}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_loop
)

echo Замена бинарного файла на актуальную версию...
move /y ""{1}"" ""{2}"" >nul

echo Запуск обновленного Google Flow Desktop...
start """" ""{2}""

echo Очистка временных файлов...
(goto) 2>nul & del ""%~f0""
exit
", currentPid, tempNewExe, currentExe);

                File.WriteAllText(updaterBat, batScript, System.Text.Encoding.GetEncoding(866));

                // Launch updater script detached
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + updaterBat + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = appDir
                };

                Process.Start(psi);

                // Exit current process
                Application.Exit();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UpdateManager] Error applying update: " + ex.Message);
                return false;
            }
        }

        private static Version ParseVersion(string ver)
        {
            try
            {
                return new Version(ver);
            }
            catch
            {
                return new Version(1, 0, 0);
            }
        }
    }
}
