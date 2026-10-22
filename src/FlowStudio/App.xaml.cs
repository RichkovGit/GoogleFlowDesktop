using System.Windows;
using System.IO;
using FlowStudio.Services;

namespace FlowStudio;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if(e.Args is ["--apply-update",var pid,var directory]&&int.TryParse(pid,out var parentPid))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=Task.Run(async()=>{int code=1;try{code=await AppUpdateService.ApplyUpdateAsync(parentPid,directory);}catch{}finally{Dispatcher.Invoke(()=>Shutdown(code));}});return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try{Directory.CreateDirectory(ProjectStore.AppDirectory);File.WriteAllText(Path.Combine(ProjectStore.AppDirectory,"last-ui-error.txt"),args.Exception.ToString());}catch{}
            MessageBox.Show(args.Exception.Message, "Flow Studio · ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        if (e.Args.Contains("--self-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(async () =>
            {
                int code=1;
                try {code=await SelfTest.RunAsync();}
                catch(Exception ex) {try{await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(),"FlowStudio-self-test-error.txt"),ex.ToString());}catch{}}
                finally {Dispatcher.Invoke(() => Shutdown(code));}
            });
            return;
        }
        base.OnStartup(e);
        MainWindow = new MainWindow { Style=(Style)FindResource(typeof(Window)),Language=System.Windows.Markup.XmlLanguage.GetLanguage("ru-RU") };
        MainWindow.Show();
    }
}
