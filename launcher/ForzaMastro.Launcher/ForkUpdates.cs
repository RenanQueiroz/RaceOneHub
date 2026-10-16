using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ForzaMastro.Launcher;

// Fork-only (FORK.md): where this checkout stands against the fork and upstream.
public sealed record ForkUpdateInfo(string Branch, string Head, string Fork, string Upstream, int Behind, string[] Commits, int UpstreamNotTaken, bool Fetched);

public sealed record ForkScriptResult(bool Done, bool Ok, bool HubChanged, string Message, string? Error, string? Log)
{
    public static ForkScriptResult? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            bool Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            return new(Flag("done"), Flag("ok"), Flag("hub_changed"), Text("message") ?? "", Text("error"), Text("log"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

public static class ForkUpdates
{
    public static async Task<ForkUpdateInfo?> CheckAsync(string root)
    {
        if (!Directory.Exists(Path.Combine(root, ".git"))) return null;
        var branch = await Git(root, 15, "rev-parse", "--abbrev-ref", "HEAD");
        var head = await Git(root, 15, "rev-parse", "--short", "HEAD");
        if (branch is null || head is null) return null;
        var fetched = await Git(root, 60, "fetch", "--quiet", "origin") is not null;
        await Git(root, 60, "fetch", "--quiet", "upstream");
        var behind = int.TryParse(await Git(root, 15, "rev-list", "--count", "HEAD..origin/main"), out var count) ? count : 0;
        var log = behind > 0 ? await Git(root, 15, "log", "--no-merges", "--format=%s", "-n", "30", "HEAD..origin/main") : null;
        var upstream = int.TryParse(await Git(root, 15, "rev-list", "--count", "origin/main..upstream/main"), out var notTaken) ? notTaken : 0;
        return new(branch, head, Repository(await Git(root, 15, "remote", "get-url", "origin")), Repository(await Git(root, 15, "remote", "get-url", "upstream")),
                   behind, log?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [], upstream, fetched);
    }

    // fork\update.ps1 fast-forwards to the fork's main and refreshes everything but the running Hub.
    public static Process StartUpdate(string root, string progressFile) =>
        Process.Start(Script(root, "update.ps1", "-SkipHub", "-ProgressFile", progressFile)) ?? throw new IOException("Could not start the update.");

    // Publishes the Hub once this process has exited, then starts it again. The
    // shortcut already points at launcherpp, so it is left as it is.
    public static void StartHubRestart(string root, int processId) =>
        Process.Start(Script(root, "setup.ps1", "-HubOnly", "-StartHub", "-NoShortcut", "-WaitForPid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static ProcessStartInfo Script(string root, string name, params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "fork", name) }.Concat(arguments))
            info.ArgumentList.Add(argument);
        return info;
    }

    private static string Repository(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4];
        var parts = trimmed.Replace(':', '/').Split('/');
        return parts.Length >= 2 ? parts[^2] + "/" + parts[^1] : trimmed;
    }

    // Null on any failure, including no Git, no network and timeouts: update checks stay quiet.
    private static async Task<string?> Git(string root, int timeoutSeconds, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-C"); info.ArgumentList.Add(root);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["GCM_INTERACTIVE"] = "never";
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); return null; }
            await error;
            return process.ExitCode == 0 ? (await output).Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }
}
