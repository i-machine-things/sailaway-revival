using System.Runtime.InteropServices;

namespace SailawayRevival.Installer.Services;

// Sets up the server to start automatically when the game connects (Linux)
// or at login (Windows - no on-demand socket-activation equivalent exists
// there). One entry point, two platform implementations, so Windows-specific
// code stays isolated to its own method and doesn't need touching the Linux
// path that's actually testable here.
public static class AutostartInstaller
{
    public static async Task<string> InstallAsync(
        string repoDir, string venvPython, int port, Action<string> onLine, CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await InstallWindowsAsync(repoDir, venvPython, onLine, ct);
        }
        return await InstallLinuxAsync(repoDir, venvPython, port, onLine, ct);
    }

    static async Task<string> InstallLinuxAsync(
        string repoDir, string venvPython, int port, Action<string> onLine, CancellationToken ct)
    {
        var systemctl = FindOnPath("systemctl");
        if (systemctl == null)
        {
            return "systemd not available: start the server yourself before playing:\n" +
                   $"  {venvPython} {Path.Combine(repoDir, "server", "sailaway_server.py")}";
        }
        var check = await ProcessRunner.RunAsync(systemctl, "--user show-environment", onLine, ct: ct);
        if (check.ExitCode != 0)
        {
            return "systemd --user session not available: start the server yourself before playing:\n" +
                   $"  {venvPython} {Path.Combine(repoDir, "server", "sailaway_server.py")}";
        }

        var unitsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "systemd", "user");
        Directory.CreateDirectory(unitsDir);

        var socketTemplate = File.ReadAllText(Path.Combine(repoDir, "packaging", "systemd", "sailaway-revival.socket.in"));
        File.WriteAllText(Path.Combine(unitsDir, "sailaway-revival.socket"), socketTemplate.Replace("@PORT@", port.ToString()));

        var serviceTemplate = File.ReadAllText(Path.Combine(repoDir, "packaging", "systemd", "sailaway-revival.service.in"));
        File.WriteAllText(Path.Combine(unitsDir, "sailaway-revival.service"),
            serviceTemplate.Replace("@PYTHON@", venvPython).Replace("@REPO@", repoDir));

        await ProcessRunner.RunAsync(systemctl, "--user daemon-reload", onLine, ct: ct);
        await ProcessRunner.RunAsync(systemctl, "--user stop sailaway-revival.service", onLine, ct: ct); // pick up new code; may no-op
        var enable = await ProcessRunner.RunAsync(systemctl, "--user enable --now sailaway-revival.socket", onLine, ct: ct);
        if (enable.ExitCode != 0)
        {
            throw new InvalidOperationException("could not install the systemd socket; see the log above");
        }
        return "The server now starts by itself when Sailaway connects.\nLogs: journalctl --user -u sailaway-revival";
    }

    // Windows has no systemd and no easy "activate on first TCP connection"
    // primitive, so instead: start the server now, and set up a logon
    // Scheduled Task to do the same at every future login. The server then
    // just runs continuously from login to logout - deliberately different
    // from Linux's idle-exit behavior, not a bug (see server/sailaway_server.py's
    // exit_when_idle, which is wired specifically to systemd socket
    // activation and was never meant to apply generally).
    //
    // UNVERIFIED: no Windows machine to test this on. schtasks.exe is used
    // instead of the Task Scheduler COM API specifically so a failure here
    // can be handed to the user as an exact command to run themselves.
    static async Task<string> InstallWindowsAsync(
        string repoDir, string venvPython, Action<string> onLine, CancellationToken ct)
    {
        var serverScript = Path.Combine(repoDir, "server", "sailaway_server.py");
        var pythonw = venvPython.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(venvPython)!, "pythonw.exe")
            : venvPython;
        var runPythonw = File.Exists(pythonw) ? pythonw : venvPython;

        onLine("Starting the server now...");
        ProcessRunner.StartDetached(runPythonw, $"-u \"{serverScript}\"", workingDirectory: repoDir);

        var taskName = "SailawayRevivalServer";
        var taskCommand = $"/Create /TN {taskName} /TR \"\\\"{runPythonw}\\\" -u \\\"{serverScript}\\\"\" " +
                           "/SC ONLOGON /RL LIMITED /F";
        onLine($"> schtasks {taskCommand}");
        var result = await ProcessRunner.RunAsync("schtasks", taskCommand, onLine, workingDirectory: repoDir, ct: ct);
        if (result.ExitCode != 0)
        {
            return "The server is running now, but the autostart-at-login task could not be created " +
                   "automatically. Run this yourself to make it permanent:\n" +
                   $"  schtasks {taskCommand}";
        }
        return "The server is running, and will start automatically every time you log in.";
    }

    static string? FindOnPath(string exeName)
    {
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
}
