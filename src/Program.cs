using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "GoogleFlowDesktop_SingleInstance_v2", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("Google Flow Desktop уже запущен.", "Google Flow Desktop",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log(logPath, "UnhandledException", e.ExceptionObject);
                };

                Application.ThreadException += (s, e) =>
                {
                    Log(logPath, "ThreadException", e.Exception);
                };

                try
                {
                    GpuConfig.SetProcessDPIAware();
                }
                catch { }

                try
                {
                    Theme.EnableDarkMode();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm());
                }
                catch (Exception ex)
                {
                    Log(logPath, "Main", ex);
                    MessageBox.Show(
                        "Не удалось запустить Google Flow Desktop.\r\n\r\n" + ex.Message +
                        "\r\n\r\nПодробности: " + logPath,
                        "Google Flow Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static void Log(string path, string tag, object ex)
        {
            try
            {
                File.AppendAllText(path,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + tag + ": " + ex + "\r\n");
            }
            catch { }
        }
    }
}
