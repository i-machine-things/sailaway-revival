using Avalonia;
using SailawayRevival.Installer.Services;

namespace SailawayRevival.Installer;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless diagnostic: lets each Services/*.cs piece be verified
        // without a display, useful since most of this app's real risk is
        // in OS-integration logic, not the UI itself.
        if (args.Contains("--self-test"))
        {
            var game = SteamLocator.FindGame();
            Console.WriteLine(game != null ? $"SteamLocator.FindGame() -> {game}" : "SteamLocator.FindGame() -> not found");
            return game != null ? 0 : 1;
        }
        if (args.Contains("--test-dotnet"))
        {
            var dotnet = DotnetBootstrap.FindDotnet();
            Console.WriteLine(dotnet != null ? $"DotnetBootstrap.FindDotnet() -> {dotnet}" : "not found");
            return dotnet != null ? 0 : 1;
        }
        if (args.Contains("--test-uv"))
        {
            var uv = PythonEnvSetup.FindUv();
            Console.WriteLine(uv != null ? $"PythonEnvSetup.FindUv() -> {uv}" : "not found");
            return 0;
        }
        if (args.Contains("--test-python-setup"))
        {
            var uv = PythonEnvSetup.FindUv();
            if (uv == null)
            {
                uv = PythonEnvSetup.InstallUvAsync(Console.WriteLine).GetAwaiter().GetResult();
            }
            var repoDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            PythonEnvSetup.SetUpAsync(uv, repoDir, Console.WriteLine).GetAwaiter().GetResult();
            Console.WriteLine("done");
            return 0;
        }
        if (args.Contains("--test-patch"))
        {
            var dotnet = DotnetBootstrap.FindDotnet() ?? throw new InvalidOperationException("no dotnet found");
            var game = SteamLocator.FindGame() ?? throw new InvalidOperationException("no game found");
            var managed = Path.Combine(game, "Sailaway_Data", "Managed");
            var repoDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var port = ServerConfig.PickPortAsync().GetAwaiter().GetResult();
            var server = $"127.0.0.1:{port}/sw";
            Console.WriteLine($"dotnet={dotnet} game={game} repo={repoDir} server={server}");
            ModPatcher.BuildAndPatchAsync(dotnet, repoDir, managed, server, Console.WriteLine).GetAwaiter().GetResult();
            Console.WriteLine("done");
            return 0;
        }
        if (args.Contains("--test-autostart"))
        {
            var repoDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var venvPython = Path.Combine(repoDir, ".venv", "bin", "python");
            var port = ServerConfig.PickPortAsync().GetAwaiter().GetResult();
            var message = AutostartInstaller.InstallAsync(repoDir, venvPython, port, Console.WriteLine).GetAwaiter().GetResult();
            Console.WriteLine(message);
            return 0;
        }
        if (args.Contains("--test-vdf"))
        {
            var vdfPath = args[Array.IndexOf(args, "--test-vdf") + 1];
            var root = VdfParser.Parse(File.ReadAllText(vdfPath));
            foreach (var p in VdfParser.CollectValues(root, "path"))
            {
                Console.WriteLine($"path -> {p}");
            }
            return 0;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
