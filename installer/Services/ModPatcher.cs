namespace SailawayRevival.Installer.Services;

// Builds mod/ against the player's own game files, then runs the patcher -
// in that exact order, never in parallel. This ordering is load-bearing, not
// stylistic: mod/SailawayRevival.Mod.csproj prefers compiling against
// Assembly-CSharp.dll.orig if it exists, and the patcher is what *creates*
// .orig on first run. Both steps still shell out to the dotnet CLI (same as
// install.sh) rather than being called in-process, so a crash in either one
// is isolated to a subprocess with a clean exit code instead of taking this
// installer down mid-write to the user's only copy of the game's DLL.
public static class ModPatcher
{
    public static async Task BuildAndPatchAsync(
        string dotnetPath, string repoDir, string managedDir, string server, Action<string> onLine,
        CancellationToken ct = default)
    {
        if (IsGameRunning())
        {
            throw new InvalidOperationException("Sailaway is running. Close the game, then try again.");
        }

        var buildOut = Path.Combine(repoDir, "build", "mod");
        onLine($"Building the mod against {managedDir}...");
        var build = await ProcessRunner.RunAsync(
            dotnetPath,
            $"build mod -c Release -p:GameManaged=\"{managedDir}\" -o \"{buildOut}\" --nologo -v quiet",
            onLine, workingDirectory: repoDir, ct: ct);
        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException("building the mod failed; see the log above");
        }

        onLine($"Patching the game (server {server})...");
        var modDll = Path.Combine(buildOut, "SailawayRevival.Mod.dll");
        var patch = await ProcessRunner.RunAsync(
            dotnetPath,
            $"run --project patcher -- \"{managedDir}\" \"{server}\" --mod \"{modDll}\"",
            onLine, workingDirectory: repoDir, ct: ct);
        if (patch.ExitCode != 0)
        {
            // Mirror patcher/Program.cs's own exit codes rather than a generic failure.
            var reason = patch.ExitCode switch
            {
                3 => "Sailaway is running. Close the game, then try again.",
                2 => "could not replace the game's DLL (it may still be open). Close Sailaway and try again.",
                1 => "the patcher was called incorrectly - this is a bug in the installer, not something you can fix.",
                _ => "see the log above",
            };
            throw new InvalidOperationException($"patching failed: {reason}");
        }
    }

    static bool IsGameRunning() => System.Diagnostics.Process.GetProcesses().Any(p =>
    {
        try
        {
            return p.ProcessName.Equals("Sailaway", StringComparison.OrdinalIgnoreCase)
                || p.ProcessName.Equals("Sailaway.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // a process that exited between enumeration and this check
        }
    });
}
