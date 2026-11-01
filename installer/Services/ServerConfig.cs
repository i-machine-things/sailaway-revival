using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace SailawayRevival.Installer.Services;

// Picks the local port the game is patched to talk to, and writes
// config.toml for the server to read - same logic as install.sh, including
// the part that's easy to get wrong: distinguishing "this port is held by
// our own already-running server" from "something else has it," not just
// "is it free." Getting that distinction wrong means every re-run bumps the
// port needlessly instead of reusing the one already in use.
public static class ServerConfig
{
    const int DefaultPort = 47652;

    // Must match CONFIG_FILE in server/sailaway_server.py on every OS: that
    // code always uses ~/.config (or $XDG_CONFIG_HOME), unconditionally,
    // never an OS-specific special folder. SpecialFolder.ApplicationData
    // would silently diverge on Windows (%APPDATA%, not ~/.config), causing
    // the server to fall back to the default port while the game is patched
    // for whatever port this picked - a CodeRabbit catch on PR #6.
    public static string ConfigDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "sailaway-revival");

    static string ConfigPath => Path.Combine(ConfigDir, "config.toml");

    public static async Task<int> PickPortAsync()
    {
        int port = ReadExistingPort() ?? DefaultPort;
        for (int tries = 0; !IsFree(port) && !await IsOursAsync(port); tries++)
        {
            if (tries >= 100)
            {
                throw new InvalidOperationException($"no free port found near {port}");
            }
            port++;
        }
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath,
            "# written by the installer; the game is patched for this port\n" +
            $"port = {port}\n");
        return port;
    }

    static int? ReadExistingPort()
    {
        if (!File.Exists(ConfigPath))
        {
            return null;
        }
        var match = Regex.Match(File.ReadAllText(ConfigPath), @"^port\s*=\s*(\d+)", RegexOptions.Multiline);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    static async Task<bool> IsOursAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var body = await client.GetStringAsync($"http://127.0.0.1:{port}/sw/cgi-bin/sailaway/GetAllBadges.pl");
            return body.Contains("\"badges\"");
        }
        catch
        {
            return false;
        }
    }
}
