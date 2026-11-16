using System.Windows;

namespace Glimpse.UI;

public partial class App : Application
{
    public bool LaunchMainWindow { get; set; } = true;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"操作未完成：{args.Exception.GetType().Name}。请重试或重启应用。", "Glimpse");
            args.Handled = true;
        };
        if (LaunchMainWindow) new MainWindow().Show();
    }
}
