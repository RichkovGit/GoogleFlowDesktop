using System;
using System.IO;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    File.AppendAllText(logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] UnhandledException: " + e.ExceptionObject.ToString() + "\n");
                }
                catch { }
            };

            Application.ThreadException += (s, e) =>
            {
                try
                {
                    File.AppendAllText(logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] ThreadException: " + e.Exception.ToString() + "\n");
                }
                catch { }
            };

            try
            {
                GpuConfig.SetProcessDPIAware();
            }
            catch { }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Main Catch: " + ex.ToString() + "\n");
                }
                catch { }
            }
        }
    }
}
