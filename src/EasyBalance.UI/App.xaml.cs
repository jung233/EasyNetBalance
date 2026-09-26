using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using EasyBalance.Shared;

namespace EasyBalance.UI;

public partial class App : Application
{
    private static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EasyBalance");
    private static string ServicePath => Path.Combine(InstallDirectory, "EasyBalance.Service.exe");
    private static string CorePath => Path.Combine(InstallDirectory, "core", "sing-box.exe");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => { Record(args.Exception); MessageBox.Show(args.Exception.Message, "EasyBalance", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        if (e.Args.Contains("--install-payload", StringComparer.OrdinalIgnoreCase))
        {
            try { InstallPayload(); Shutdown(0); }
            catch (Exception exception) { Record(exception); Shutdown(1); }
            return;
        }
        try
        {
            await EnsureServiceAsync();
            new MainWindow().Show();
        }
        catch (Exception exception)
        {
            Record(exception);
            MessageBox.Show(exception.Message, "EasyBalance startup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task EnsureServiceAsync()
    {
        if (!await IsPipeAvailableAsync() || !IsInstalledPayloadCurrent())
        {
            using var setup = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable."),
                Arguments = "--install-payload", Verb = "runas", UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Could not start service setup.");
            await setup.WaitForExitAsync();
            if (setup.ExitCode != 0) throw new InvalidOperationException("Service installation failed. Approve the administrator prompt and try again.");
        }
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await IsPipeAvailableAsync()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("The EasyBalance service did not become available.");
    }

    private static async Task<bool> IsPipeAvailableAsync()
    {
        await using var pipe = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(400); return pipe.IsConnected; }
        catch (Exception exception) when (exception is TimeoutException or IOException) { return false; }
    }

    private static bool IsInstalledPayloadCurrent() =>
        Matches("EasyBalance.Payload.Service.exe", ServicePath) && Matches("EasyBalance.Payload.core.sing-box.exe", CorePath);

    private static bool Matches(string resourceName, string path)
    {
        if (!File.Exists(path)) return false;
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (source is null) return false;
        using var target = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(source).SequenceEqual(SHA256.HashData(target));
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
                using var processes = new ProcessCollection(Process.GetProcessesByName("EasyBalance.Service"));
                if (processes.Items.All(process => process.HasExited)) break;
                if (attempt == 59) throw new TimeoutException("The previous service did not stop.");
                Thread.Sleep(500);
            }
        }
        WriteResource("EasyBalance.Payload.Service.exe", ServicePath);
        WriteResource("EasyBalance.Payload.core.sing-box.exe", CorePath);
        var parameters = "binPath= \"" + ServicePath + "\" start= auto DisplayName= \"EasyBalance routing service\"";
        RunSc((installed ? "config" : "create") + " EasyBalance " + parameters);
        RunSc("start EasyBalance", 1056);
    }

    private sealed class ProcessCollection(Process[] items) : IDisposable
    {
        public Process[] Items { get; } = items;
        public void Dispose() { foreach (var item in Items) item.Dispose(); }
    }

    private static void WriteResource(string resourceName, string path)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Release payload is missing: " + resourceName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        source.CopyTo(target);
    }

    private static int RunSc(string arguments, params int[] allowed)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Could not launch Service Control Manager.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowed.Contains(process.ExitCode))
            throw new InvalidOperationException($"Service command failed ({arguments}): {output} {error}");
        return process.ExitCode;
    }

    private static void Record(Exception exception)
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
