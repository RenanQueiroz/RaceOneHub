using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForzaMastro.Launcher;

// Fork-only (FORK.md): PLAY keeps one generated game folder up to date in place.
// The backend builds into an empty sibling folder; that folder then replaces the
// old one. Nothing here changes what the backend itself writes.
public static class ForkOutput
{
    public const string StampName = "fork-build.json";
    // Inputs whose change changes what a build writes: the prepared runtime and the
    // backend code that copies, hydrates and configures it.
    private static readonly string[] RuntimeInputs = [
        "runtime/pc/ForzaMastro5.exe", "runtime/pc/ForzaMastro5Bridge.dll", "runtime/pc/data", "runtime/xg-host",
        "runtime/bridge-template.json", "tools/pc_launcher_backend.py", "tools/fm5_game_files.py", "tools/raceone_games.py",
        "tools/fm5_language_catalog.py", "tools/pc_icon_resources.py", "tools/fm5_bridge_template.py",
        "launcher/ForzaMastro.Launcher/Assets/ForzaMotorsport5.ico"];
    // Settings the native pre-game screen writes to data/RaceOneEngine.ini, in the Hub's ids.
    private static readonly string[] LauncherKeys = ["resolution", "frame_rate", "antialiasing", "graphics", "display", "vsync", "language", "audio"];

    public static string RuntimeStamp(string root)
    {
        var lines = new List<string>();
        foreach (var input in RuntimeInputs)
        {
            var path = Path.Combine(root, input);
            IEnumerable<string> files = Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
                      : File.Exists(path) ? [path] : Enumerable.Empty<string>();
            lines.AddRange(files.Select(file => Path.GetRelativePath(root, file).Replace('\\', '/') + ":" + Sha256(File.ReadAllBytes(file))));
            if (!files.Any()) lines.Add(input + ":missing");
        }
        return Sha256(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
    }

    // The settings the game will use: data/RaceOneEngine.ini, which the pre-game screen
    // updates, plus forzamastro.ini's pre-game switch. Null when there is no game.
    public static Dictionary<string, string>? ReadEffective(string output)
    {
        var engine = ReadIni(Path.Combine(output, "data", "RaceOneEngine.ini"));
        var startup = ReadIni(Path.Combine(output, "forzamastro.ini"));
        if (!File.Exists(Path.Combine(output, LauncherModel.GeneratedExecutableName)) || engine is null || startup is null ||
            !engine.TryGetValue("launcher", out var launcher) || !startup.TryGetValue("startup", out var start)) return null;
        var values = new Dictionary<string, string>();
        foreach (var key in LauncherKeys)
            if (launcher.TryGetValue(key, out var value)) values[key] = key == "vsync" ? (value is "on" or "true" or "1" ? "true" : "false") : value;
        if (start.TryGetValue("show_pre_game", out var pre)) values["show_pre_game"] = pre == "0" ? "false" : "true";
        return values;
    }

    public static string? ReadGameFolder(string output) =>
        ReadIni(Path.Combine(output, "data", "RaceOneEngine.ini")) is { } ini && ini.TryGetValue("launcher", out var launcher) &&
        launcher.TryGetValue("game_folder", out var folder) ? folder : null;

    // The settings the folder was built with (build-profile.json), in the Hub's ids.
    public static Dictionary<string, string>? ReadBuilt(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "build-profile.json")));
            if (!document.RootElement.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object) return null;
            return settings.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? p.Value.GetBoolean().ToString().ToLowerInvariant() : p.Value.ToString());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static string? ReadStamp(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, StampName)));
            return document.RootElement.TryGetProperty("runtime", out var runtime) ? runtime.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void WriteStamp(string output, string runtime) =>
        File.WriteAllText(Path.Combine(output, StampName), JsonSerializer.Serialize(new { runtime, built_utc = DateTime.UtcNow.ToString("o") }, LauncherModel.Json));

    // Null when the folder can be played as it is; otherwise why PLAY builds first.
    public static string? StaleReason(IReadOnlyDictionary<string, string> wanted, string gameFolder, string output, string runtime)
    {
        if (!File.Exists(Path.Combine(output, LauncherModel.GeneratedExecutableName))) return "No game built yet.";
        if (ReadStamp(output) is not { } stamp) return "The game needs a fresh build.";
        if (stamp != runtime) return "The runtime changed.";
        var folder = ReadGameFolder(output);
        if (folder is null || !string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(gameFolder), StringComparison.OrdinalIgnoreCase))
            return "The game folder changed.";
        var effective = ReadEffective(output);
        if (effective is null) return "The game's settings are missing.";
        return wanted.Any(pair => !effective.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase))
            ? "Settings changed." : null;
    }

    public static string Signature(string output, IReadOnlyDictionary<string, string> effective) =>
        Path.GetFullPath(output).ToUpperInvariant() + "|" + string.Join(";", effective.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    // A game started from this exact folder. One that cannot be inspected counts as running.
    public static bool IsRunning(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        foreach (var process in Process.GetProcessesByName(name))
            using (process)
            {
                try { if (string.Equals(process.MainModule?.FileName, Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return true; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { if (!process.HasExited) return true; }
            }
        return false;
    }

    // Logs always; shader, pipeline and layout caches only when the runtime that made them is unchanged.
    public static void Carry(string from, string to, string runtime)
    {
        if (!Directory.Exists(from)) return;
        CopyMissing(Path.Combine(from, "data", "logs"), Path.Combine(to, "data", "logs"));
        if (ReadStamp(from) != runtime) return;
        CopyMissing(Path.Combine(from, "data", "cache"), Path.Combine(to, "data", "cache"));
        CopyMissing(Path.Combine(from, "data", "titles"), Path.Combine(to, "data", "titles"));
    }

    private static void CopyMissing(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    public static string NextFolder(string output) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)) + ".next";
    public static string OldFolder(string output) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)) + ".old";

    public static void RemoveLeftover(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    // Puts the new folder in place of the old one; the old one comes back if that fails.
    public static void Swap(string next, string output)
    {
        var old = OldFolder(output);
        RemoveLeftover(old);
        var hadOld = Directory.Exists(output);
        if (hadOld) Directory.Move(output, old);
        try { Directory.Move(next, output); }
        catch
        {
            if (hadOld && !Directory.Exists(output)) Directory.Move(old, output);
            throw;
        }
        try { RemoveLeftover(old); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // Removed before the next swap.
    }

    private static Dictionary<string, Dictionary<string, string>>? ReadIni(string path)
    {
        if (!File.Exists(path)) return null;
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? section = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (!result.TryGetValue(name, out section)) result[name] = section = new(StringComparer.OrdinalIgnoreCase);
            }
            else if (section is not null && line.IndexOf('=') > 0)
                section[line[..line.IndexOf('=')].Trim()] = line[(line.IndexOf('=') + 1)..].Trim();
        }
        return result;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
