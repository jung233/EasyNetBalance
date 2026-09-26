using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EasyBalance.Service.Core;

/// <summary>
/// The features of the actual sing-box executable selected by the user.
/// </summary>
public sealed class SingBoxCapabilities
{
    public static Version MinimumSupportedVersion { get; } = new(1, 11, 0);

    public required string VersionText { get; init; }
    public required Version Version { get; init; }
    public required IReadOnlySet<string> BuildTags { get; init; }

    public bool SupportsTunAddressArray => Version >= new Version(1, 10, 0);
    public bool SupportsRouteAction => Version >= new Version(1, 11, 0);
    public bool SupportsIPv4IPv6RuleMatching => Version >= new Version(1, 5, 0);
    public bool SupportsProcessPath => Version >= new Version(1, 5, 0);
    public bool SupportsSelectorRuntimeControl => Version >= new Version(1, 8, 0) && SupportsClashApi;
    public bool SupportsDirectOutboundDelayTest => SupportsClashApi;
    public bool SupportsClashApi => BuildTags.Contains("with_clash_api");

    public bool IsCompatible =>
        Version >= MinimumSupportedVersion &&
        SupportsTunAddressArray &&
        SupportsRouteAction &&
        SupportsIPv4IPv6RuleMatching &&
        SupportsProcessPath &&
        SupportsSelectorRuntimeControl;

    public void EnsureCompatible()
    {
        if (IsCompatible)
        {
            return;
        }

        var missing = new List<string>();
        if (Version < MinimumSupportedVersion)
        {
            missing.Add($"version {MinimumSupportedVersion} or newer (detected {VersionText})");
        }

        if (!SupportsClashApi)
        {
            missing.Add("the with_clash_api build tag for selector control and outbound tests");
        }

        throw new SingBoxCompatibilityException(
            $"The selected sing-box executable is not compatible. EasyBalance requires {string.Join(" and ", missing)}.");
    }
}

public sealed class SingBoxCompatibilityException(string message) : InvalidOperationException(message);

/// <summary>
/// Runs the selected binary's version command to discover its real version and build tags.
/// </summary>
public sealed class SingBoxCapabilitiesDetector
{
    private static readonly Regex VersionPattern = new(
        @"sing-box\s+version\s+v?(?<version>\d+\.\d+\.\d+)(?![\w.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TagsPattern = new(
        @"(?m)^Tags:\s*(?<tags>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<SingBoxCapabilities> DetectAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The selected sing-box executable was not found.", executablePath);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? AppContext.BaseDirectory
            }
        };
        process.StartInfo.ArgumentList.Add("version");

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("sing-box version command could not be started.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("Could not start the selected sing-box executable.", exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await standardOutputTask.ConfigureAwait(false);
            var error = await standardErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"sing-box version exited with code {process.ExitCode}: {TrimForError(error)}");
            }

            var versionMatch = VersionPattern.Match(output);
            if (!versionMatch.Success || !System.Version.TryParse(versionMatch.Groups["version"].Value, out var version))
            {
                throw new InvalidOperationException("sing-box version output did not contain a recognizable stable version.");
            }

            var tagsMatch = TagsPattern.Match(output);
            var tags = tagsMatch.Success
                ? tagsMatch.Groups["tags"].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return new SingBoxCapabilities
            {
                VersionText = versionMatch.Groups["version"].Value,
                Version = version,
                BuildTags = tags
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("sing-box version command did not finish within eight seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static string TrimForError(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between HasExited and Kill.
        }
    }
}
