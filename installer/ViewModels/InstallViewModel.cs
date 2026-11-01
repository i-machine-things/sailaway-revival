using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SailawayRevival.Installer.Services;

namespace SailawayRevival.Installer.ViewModels;

// Drives the install step sequence. Kept deliberately simple (no command
// framework, no DI container) - this is a small, linear wizard, not an app
// that needs either.
public class InstallViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Log { get; } = new();

    string _status = "Ready.";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    double _progress;
    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    bool _needsManualGamePath;
    public bool NeedsManualGamePath
    {
        get => _needsManualGamePath;
        set { _needsManualGamePath = value; OnPropertyChanged(); }
    }

    bool _installing;
    public bool Installing
    {
        get => _installing;
        set { _installing = value; OnPropertyChanged(); }
    }

    public string? GamePath { get; set; }

    void AppendLog(string line)
    {
        // Avalonia's dispatcher requirement for cross-thread UI updates is
        // handled by the caller awaiting on the UI thread throughout (no
        // background Task.Run is used), so this can update the collection
        // directly.
        Log.Add(line);
    }

    public async Task RunInstallAsync()
    {
        Installing = true;
        Log.Clear();
        try
        {
            Status = "Looking for Sailaway...";
            Progress = 5;
            var game = GamePath ?? SteamLocator.FindGame();
            if (game == null)
            {
                AppendLog("Could not find Sailaway automatically.");
                NeedsManualGamePath = true;
                Status = "Browse for the Sailaway folder, then try again.";
                return;
            }
            GamePath = game;
            NeedsManualGamePath = false;
            AppendLog($"Found Sailaway: {game}");
            var managed = Path.Combine(game, "Sailaway_Data", "Managed");
            if (!File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")))
            {
                AppendLog($"{managed} doesn't look like a Sailaway install (no Assembly-CSharp.dll).");
                NeedsManualGamePath = true;
                Status = "That folder doesn't look right - browse for the Sailaway folder.";
                return;
            }

            Status = "Checking for the .NET SDK...";
            Progress = 15;
            var dotnet = DotnetBootstrap.FindDotnet();
            if (dotnet == null)
            {
                AppendLog("Installing the .NET 8 SDK (no root needed)...");
                dotnet = await DotnetBootstrap.InstallAsync(AppendLog);
            }
            AppendLog($"Using dotnet: {dotnet}");

            Status = "Picking a port...";
            Progress = 25;
            var port = await ServerConfig.PickPortAsync();
            var server = $"127.0.0.1:{port}/sw";
            AppendLog($"Server address: {server}");

            Status = "Building the mod and patching the game...";
            Progress = 35;
            var repoDir = FindRepoRoot();
            await ModPatcher.BuildAndPatchAsync(dotnet, repoDir, managed, server, AppendLog);

            Status = "Setting up the Python server environment...";
            Progress = 65;
            var uv = PythonEnvSetup.FindUv();
            if (uv == null)
            {
                AppendLog("Installing uv (no root needed)...");
                uv = await PythonEnvSetup.InstallUvAsync(AppendLog);
            }
            await PythonEnvSetup.SetUpAsync(uv, repoDir, AppendLog);

            Status = "Setting up autostart...";
            Progress = 85;
            var venvPython = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(repoDir, ".venv", "Scripts", "python.exe")
                : Path.Combine(repoDir, ".venv", "bin", "python");
            var autostartMessage = await AutostartInstaller.InstallAsync(repoDir, venvPython, port, AppendLog);
            AppendLog(autostartMessage);

            Progress = 100;
            Status = "Done. Start Sailaway from Steam.";
        }
        catch (Exception e)
        {
            AppendLog($"error: {e.Message}");
            Status = "Something went wrong - see the log above.";
        }
        finally
        {
            Installing = false;
        }
    }

    static string FindRepoRoot()
    {
        // This installer always ships as part of the repo checkout (not a
        // standalone download that fetches sources separately), so walking
        // up from the running assembly to find server/sailaway_server.py is
        // reliable rather than a guess.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "server", "sailaway_server.py")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("could not find the repo root from " + AppContext.BaseDirectory);
    }

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
