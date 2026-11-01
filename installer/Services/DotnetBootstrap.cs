using System.Runtime.InteropServices;

namespace SailawayRevival.Installer.Services;

// Finds or installs the .NET 8 SDK - needed to build mod/ against the
// player's own game files and to run the patcher, same as install.sh.
// Bootstraps via Microsoft's own official, no-root installer scripts, same
// vendor/URL install.sh already uses - just invoked per-OS instead of always
// bash.
public static class DotnetBootstrap
{
    // Where install.sh and this installer both put a bootstrapped SDK, so
    // re-runs (and install.sh runs on the same machine) find the same one.
    static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");

    // A "dotnet" on PATH can be a runtime-only install (common - plenty of
    // machines have the ASP.NET/.NET runtime for some other app but not the
    // SDK). Accepting one of those here means ModPatcher's later `dotnet
    // build` fails with no SDK bootstrap ever having run - a CodeRabbit
    // catch on PR #6. So this has to actually run the candidate and check
    // `--list-sdks`, not just confirm the file exists.
    public static async Task<string?> FindDotnet()
    {
        var onPath = FindOnPath();
        if (onPath != null && await HasRequiredSdkAsync(onPath))
        {
            return onPath;
        }
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var defaultPath = Path.Combine(DefaultInstallDir, exeName);
        if (File.Exists(defaultPath) && await HasRequiredSdkAsync(defaultPath))
        {
            return defaultPath;
        }
        return null;
    }

    static async Task<bool> HasRequiredSdkAsync(string dotnetExe)
    {
        var lines = new List<string>();
        var result = await ProcessRunner.RunAsync(dotnetExe, "--list-sdks", lines.Add);
        return result.ExitCode == 0 && lines.Any(l => l.StartsWith("8."));
    }

    static string? FindOnPath()
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    // Returns the path to the now-installed dotnet executable. Throws on
    // failure - this is a required step, there's no graceful degradation.
    public static async Task<string> InstallAsync(Action<string> onLine, CancellationToken ct = default)
    {
        var installDir = DefaultInstallDir;
        ProcessRunner.Result result;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var command = $"irm https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\\dotnet-install.ps1; " +
                          $"& $env:TEMP\\dotnet-install.ps1 -Channel 8.0 -InstallDir '{installDir}'";
            onLine($"> powershell -Command \"{command}\"");
            result = await ProcessRunner.RunAsync(
                "powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"", onLine, ct: ct);
        }
        else
        {
            var command = $"curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir \"{installDir}\"";
            onLine($"> bash -c \"{command}\"");
            result = await ProcessRunner.RunAsync("bash", $"-c \"{command}\"", onLine, ct: ct);
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "could not install the .NET 8 SDK automatically; install it yourself: https://dotnet.microsoft.com/download");
        }
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        return Path.Combine(installDir, exeName);
    }
}
