using System.Windows;
using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace EasyBalance.UI;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
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
        if (e.Args.Contains("--install-payload", StringComparer.OrdinalIgnoreCase))
        {
            try { InstallPayload(); Environment.ExitCode = 0; }
            catch (Exception exception) { LogStartupException(exception); Environment.ExitCode = 1; }
            Shutdown(Environment.ExitCode);
            return;
        }

        try
        {
            await EnsureServiceAsync();
            new MainWindow().Show();
        }
        catch (Exception exception)
        {
            LogStartupException(exception);
            MessageBox.Show(exception.Message, "EasyBalance startup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EasyBalance");

    private static string ServicePath => Path.Combine(InstallDirectory, "EasyBalance.Service.exe");
    private static string CorePath => Path.Combine(InstallDirectory, "core", "sing-box.exe");

    private static async Task EnsureServiceAsync()
    {
        if (!await IsPipeAvailableAsync() || !IsInstalledPayloadCurrent())
        {
            using var elevated = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException("The UI executable path is unavailable."),
                Arguments = "--install-payload",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Windows could not start the elevated EasyBalance setup.");
            await elevated.WaitForExitAsync();
            if (elevated.ExitCode != 0) throw new InvalidOperationException("EasyBalance could not install its background service. Approve the administrator prompt and try again.");
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await IsPipeAvailableAsync()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("The EasyBalance background service did not become available.");
    }

    private static async Task<bool> IsPipeAvailableAsync()
    {
        await using var pipe = new NamedPipeClientStream(".", EasyBalance.Shared.IpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(400); return pipe.IsConnected; }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    private static void InstallPayload()
    {
        Directory.CreateDirectory(InstallDirectory);
        var installed = RunSc("query EasyBalance", 1060) != 1060;
        if (installed)
        {
            RunSc("stop EasyBalance", 1062);
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var processes = Process.GetProcessesByName("EasyBalance.Service");
                try
                {
                    if (processes.All(process => process.HasExited)) break;
                }
                finally { foreach (var process in processes) process.Dispose(); }
                if (attempt == 59) throw new TimeoutException("The previous EasyBalance service did not stop in time.");
                Thread.Sleep(500);
            }
        }
        WriteResource("EasyBalance.Payload.Service.exe", ServicePath);
        WriteResource("EasyBalance.Payload.core.sing-box.exe", CorePath);
        var serviceArguments = "binPath= \"" + ServicePath + "\" start= auto DisplayName= \"EasyBalance routing service\"";
        if (!installed)
            RunSc("create EasyBalance " + serviceArguments);
        else
            RunSc("config EasyBalance " + serviceArguments);
        RunSc("start EasyBalance", 1056);
    }

    private static bool IsInstalledPayloadCurrent() =>
        MatchesResource("EasyBalance.Payload.Service.exe", ServicePath) &&
        MatchesResource("EasyBalance.Payload.core.sing-box.exe", CorePath);

    private static bool MatchesResource(string resourceName, string path)
    {
        if (!File.Exists(path)) return false;
        using var packaged = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (packaged is null) return false;
        using var installed = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(packaged).SequenceEqual(SHA256.HashData(installed));
    }

    private static void WriteResource(string resourceName, string destination)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The release payload is missing {resourceName}.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
        source.CopyTo(target);
    }

    private static int RunSc(string arguments, params int[] allowedExitCodes)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Windows could not start the Service Control Manager.");
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowedExitCodes.Contains(process.ExitCode))
            throw new InvalidOperationException($"Service command failed ({arguments}, exit {process.ExitCode}): {process.StandardOutput.ReadToEnd()} {process.StandardError.ReadToEnd()}");
        return process.ExitCode;
    }

    private static void LogStartupException(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyBalance", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "ui-startup.log"), $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
}
