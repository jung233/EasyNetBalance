using System.Windows;
using System.IO;
using System.Text;

namespace EasyBalance.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyBalance", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "ui-startup.log"), $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{args.Exception}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { }
            MessageBox.Show(args.Exception.Message, "EasyBalance UI error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
