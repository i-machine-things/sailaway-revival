using System.Runtime.InteropServices;

namespace SailawayRevival.Installer.Services;

// Sets up the server's Python environment - same uv-first approach as
// install.sh. uv matters especially on Windows, where a system Python isn't
// guaranteed to exist at all (unlike Linux): uv can provision its own Python
// with nothing pre-installed, so this is the one dependency that's *more*
// important to get right cross-platform, not less.
public static class PythonEnvSetup
{
    public static string? FindUv()
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "uv.exe" : "uv";
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(home, ".local", "bin", exeName);
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    public static async Task<string> InstallUvAsync(Action<string> onLine, CancellationToken ct = default)
    {
        ProcessRunner.Result result;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var command = "irm https://astral.sh/uv/install.ps1 | iex";
            onLine($"> powershell -Command \"{command}\"");
            result = await ProcessRunner.RunAsync(
                "powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"", onLine, ct: ct);
        }
        else
        {
            var command = "curl -LsSf https://astral.sh/uv/install.sh | sh";
            onLine($"> sh -c \"{command}\"");
            result = await ProcessRunner.RunAsync("sh", $"-c \"{command}\"", onLine, ct: ct);
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "could not install uv automatically; install Python yourself (with venv support) or uv: https://docs.astral.sh/uv/");
        }
        var uv = FindUv();
        return uv ?? throw new InvalidOperationException("uv installed but not found afterward; open a new shell and try again");
    }

    // Creates .venv and installs server/requirements.txt via uv. Assumes uv
    // is already available (call InstallUvAsync first if FindUv() is null).
    public static async Task SetUpAsync(string uvPath, string repoDir, Action<string> onLine, CancellationToken ct = default)
    {
        var venv = Path.Combine(repoDir, ".venv");
        if (!Directory.Exists(venv))
        {
            var r = await ProcessRunner.RunAsync(uvPath, "venv -q .venv", onLine, workingDirectory: repoDir, ct: ct);
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException("uv venv failed; see the log above");
            }
        }
        var req = Path.Combine(repoDir, "server", "requirements.txt");
        var install = await ProcessRunner.RunAsync(
            uvPath, $"pip install -q -p .venv -r \"{req}\"", onLine, workingDirectory: repoDir, ct: ct);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException("uv pip install failed; see the log above");
        }
    }
}
