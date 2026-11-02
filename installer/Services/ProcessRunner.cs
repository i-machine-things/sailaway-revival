using System.Diagnostics;

namespace SailawayRevival.Installer.Services;

// Runs a command and streams its stdout/stderr line-by-line to a callback
// (the log pane, in practice). Every shell-out step in this app goes through
// here so the user sees the same thing install.sh's terminal output would
// have shown, line for line, rather than a silent black box.
public static class ProcessRunner
{
    public record Result(int ExitCode, string Output);

    public static async Task<Result> RunAsync(
        string fileName, string arguments, Action<string> onLine,
        string? workingDirectory = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        using var process = new Process { StartInfo = psi };
        var allOutput = new System.Text.StringBuilder();
        void Handle(string? line)
        {
            if (line == null)
            {
                return;
            }
            allOutput.AppendLine(line);
            onLine(line);
        }
        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        return new Result(process.ExitCode, allOutput.ToString());
    }

    // Starts a long-lived process (the Python server, on Windows where
    // there's no systemd to manage its lifetime) and does NOT wait for it -
    // it's meant to keep running after this installer exits. Not tracked or
    // captured, deliberately: killing it when the installer's own process
    // object goes out of scope would defeat the point.
    public static Process StartDetached(string fileName, string arguments, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException($"could not start {fileName}");
    }
}
