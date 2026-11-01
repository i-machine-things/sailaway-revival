using System.Runtime.InteropServices;

namespace SailawayRevival.Installer.Services;

// Finds the Sailaway install by enumerating every Steam library (via
// libraryfolders.vdf) rather than guessing fixed paths like install.sh does -
// this is the one piece of this installer that's more robust than the shell
// script it's replacing, and it's the same file format on every OS, so it's
// fully testable on Linux alone before any Windows-specific code is added.
public static class SteamLocator
{
    const long SailawayAppId = 552920;

    public static string? FindGame()
    {
        foreach (var steamRoot in CandidateSteamRoots())
        {
            foreach (var library in CandidateLibraries(steamRoot))
            {
                var game = Path.Combine(library, "steamapps", "common", "Sailaway");
                if (File.Exists(Path.Combine(game, "Sailaway_Data", "Managed", "Assembly-CSharp.dll")))
                {
                    return game;
                }
            }
        }
        return null;
    }

    // Every library this Steam install knows about: the libraryfolders.vdf
    // under each candidate Steam root, plus the root itself as a fallback in
    // case the vdf is missing or fails to parse (the root is almost always
    // also library "0" anyway, but don't depend on that).
    static IEnumerable<string> CandidateLibraries(string steamRoot)
    {
        yield return steamRoot;
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }
        Dictionary<string, object> root;
        try
        {
            root = VdfParser.Parse(File.ReadAllText(vdf));
        }
        catch
        {
            // Malformed or unexpected file - the direct steamRoot check above
            // still covers the common case, so just skip the rest quietly.
            yield break;
        }
        foreach (var path in VdfParser.CollectValues(root, "path"))
        {
            yield return path;
        }
    }

    static IEnumerable<string> CandidateSteamRoots()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var root in WindowsSteamRoots())
            {
                yield return root;
            }
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
    }

    static IEnumerable<string> WindowsSteamRoots()
    {
        // Steam's own install path can live in either registry hive
        // depending on install history/version - a per-user install only
        // shows up under HKCU, an older/per-machine one under HKLM. Check
        // both rather than assuming one; this is Windows-only code that
        // can't be run-tested here (no Windows machine), so every assumption
        // is written from documentation, not verified.
        foreach (var path in ReadWindowsRegistryPaths())
        {
            yield return path;
        }
        var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrEmpty(pf86))
        {
            yield return Path.Combine(pf86, "Steam");
        }
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(pf))
        {
            yield return Path.Combine(pf, "Steam");
        }
    }

    static IEnumerable<string> ReadWindowsRegistryPaths()
    {
#pragma warning disable CA1416 // guarded by IsOSPlatform(Windows) in the only caller
        string? hkcu = null;
        string? hklm = null;
        try
        {
            hkcu = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        }
        catch
        {
            // Registry access can fail in locked-down environments; fall
            // through to the folder-based candidates and the manual
            // browse-folder fallback the UI provides regardless.
        }
        try
        {
            hklm = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }
        catch
        {
        }
#pragma warning restore CA1416
        if (!string.IsNullOrEmpty(hkcu))
        {
            yield return hkcu;
        }
        if (!string.IsNullOrEmpty(hklm))
        {
            yield return hklm;
        }
    }
}
