using System;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

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
        public const string CurrentVersion = "1.1.0";
        private const string VersionCheckUrl = "https://raw.githubusercontent.com/RichkovGit/GoogleFlowDesktop/main/version.json";
        private static readonly HttpClient Client = new HttpClient();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static UpdateManager()
        {
            Client.Timeout = TimeSpan.FromSeconds(10);
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("GoogleFlowDesktop/" + CurrentVersion);
        }

        public static async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            UpdateInfo result = new UpdateInfo();
            result.CurrentVersion = CurrentVersion;
            result.HasUpdate = false;

            try
            {
                string jsonString = await Client.GetStringAsync(VersionCheckUrl);
                if (string.IsNullOrEmpty(jsonString))
                {
                    result.Error = "Пустой ответ сервера обновлений";
                    return result;
                }

                Dictionary<string, object> dict = Json.Deserialize<Dictionary<string, object>>(jsonString);
                if (dict == null || !dict.ContainsKey("version"))
                {
                    result.Error = "Некорректный формат version.json";
                    return result;
                }

                string latestVer = dict["version"].ToString().Trim();
                result.LatestVersion = latestVer;

                if (dict.ContainsKey("download_url")) result.DownloadUrl = dict["download_url"].ToString();
                if (dict.ContainsKey("release_date")) result.ReleaseDate = dict["release_date"].ToString();

                if (dict.ContainsKey("release_notes"))
                {
                    System.Collections.ArrayList notes = dict["release_notes"] as System.Collections.ArrayList;
                    if (notes != null)
                    {
                        foreach (object n in notes)
                        {
                            if (n != null) result.ReleaseNotes.Add(n.ToString());
                        }
                    }
                }

                result.HasUpdate = ParseVersion(latestVer) > ParseVersion(CurrentVersion);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Downloads the new binary and relaunches it through a small helper script.
        /// Returns the path of the downloaded binary on success; the caller is
        /// responsible for shutting the application down.
        /// </summary>
        public static async Task<string> DownloadAndApplyUpdateAsync(string downloadUrl, Action<int> progressCallback)
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string tempNewExe = Path.Combine(appDir, "GoogleFlowDesktop_update.tmp");
            string updaterBat = Path.Combine(appDir, "updater.bat");

            try
            {
                if (File.Exists(tempNewExe)) File.Delete(tempNewExe);

                using (HttpResponseMessage response = await Client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long totalBytes = response.Content.Headers.ContentLength ?? -1L;

                    using (System.IO.Stream stream = await response.Content.ReadAsStreamAsync())
                    using (FileStream fs = new FileStream(tempNewExe, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    {
                        byte[] buffer = new byte[65536];
                        long totalRead = 0;
                        int bytesRead;
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;
                            if (totalBytes > 0 && progressCallback != null)
                            {
                                progressCallback((int)((totalRead * 100) / totalBytes));
                            }
                        }
                    }
                }

                FileInfo fi = new FileInfo(tempNewExe);
                if (!fi.Exists || fi.Length < 65536)
                {
                    throw new Exception("Загруженный файл повреждён или слишком мал");
                }

                // Validate the PE signature ("MZ") so we never replace the app with HTML/error page.
                byte[] head = new byte[2];
                using (FileStream fs = new FileStream(tempNewExe, FileMode.Open, FileAccess.Read))
                {
                    fs.Read(head, 0, 2);
                }
                if (head[0] != 0x4D || head[1] != 0x5A)
                {
                    throw new Exception("Загруженный файл не является исполняемым (MZ)");
                }

                int currentPid = Process.GetCurrentProcess().Id;

                string bat = string.Concat(
                    "@echo off\r\n",
                    "title Google Flow Desktop Updater\r\n",
                    ":wait\r\n",
                    "tasklist /fi \"PID eq ", currentPid.ToString(), "\" 2>nul | find \"", currentPid.ToString(), "\" >nul\r\n",
                    "if not errorlevel 1 (\r\n",
                    "    timeout /t 1 /nobreak >nul\r\n",
                    "    goto wait\r\n",
                    ")\r\n",
                    ":replace\r\n",
                    "move /y \"", tempNewExe, "\" \"", currentExe, "\" >nul 2>&1\r\n",
                    "if errorlevel 1 (\r\n",
                    "    timeout /t 1 /nobreak >nul\r\n",
                    "    goto replace\r\n",
                    ")\r\n",
                    "start \"\" \"", currentExe, "\"\r\n",
                    "del \"%~f0\"\r\n");

                File.WriteAllText(updaterBat, bat, Encoding.Default);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"" + updaterBat + "\"";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WorkingDirectory = appDir;
                Process.Start(psi);

                return tempNewExe;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UpdateManager] " + ex.Message);
                throw;
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
