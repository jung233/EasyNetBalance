using System.Diagnostics;
using Xunit;

namespace EasyBalance.IntegrationTests;

public sealed class SingBoxCliTests
{
    [Fact]
    public async Task OptionalBinaryReportsVersion()
    {
        var binary = Environment.GetEnvironmentVariable("EASYBALANCE_SINGBOX_PATH");
        if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary)) return;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(binary)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("version");
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("sing-box", output, StringComparison.OrdinalIgnoreCase);
    }
}
