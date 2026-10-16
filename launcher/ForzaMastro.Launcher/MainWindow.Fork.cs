using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ForzaMastro.Launcher;

// Fork-only (FORK.md): PLAY builds the game folder in place when it is missing or
// stale and starts it; settings changed on the game's pre-game screen come back
// into the Hub; the fork's new commits are offered as an update.
public partial class MainWindow
{
    private sealed class ForkState
    {
        public string OutputSignature { get; set; } = "";
    }
    private ForkUpdateInfo? forkUpdate;
    private Process? forkGame;
    private DispatcherTimer? forkGameTimer;
    private bool forkPreview;
    private bool GameRunning => forkGame is { HasExited: false };

    private void InitializeFork(bool previewOnly)
    {
        forkPreview = previewOnly;
        // PLAY replaces BUILD EXE: it rebuilds the same folder instead of needing an empty one.
        BuildButton.Visibility = Visibility.Collapsed;
        BuildButton.IsEnabledChanged += (_, _) => UpdatePlayButton();
        UpdatePlayButton();
        if (!previewOnly) Loaded += async (_, _) => await CheckForkUpdates();
    }

    private void UpdatePlayButton() => PlayButton.IsEnabled = RebuildButton.IsEnabled = BuildButton.IsEnabled && !GameRunning;

    // Called after every backend refresh, once the option catalogs are known.
    private void ForkAfterRefresh(bool updateStatus)
    {
        if (model is null) return;
        if (updateStatus && StatusLabel.Text.StartsWith("Ready to build.", StringComparison.Ordinal))
        {
            var reason = StaleReason();
            StatusLabel.Text = (reason is null ? "Ready to play." : "Ready to play. " + reason + " PLAY builds the game first.") + StatusLabel.Text["Ready to build.".Length..];
        }
        AdoptGameSettings();
        if (GameRunning) StatusLabel.Text = model.GameTitle + " is running.";
        UpdatePlayButton();
    }

    private string? StaleReason()
    {
        if (model is null) return null;
        try { return ForkOutput.StaleReason(model.Values(), model.GameFolder, model.OutputDirectory, ForkOutput.RuntimeStamp(model.ProjectRoot)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // The pre-game screen writes the game folder's data/RaceOneEngine.ini. Settings
    // changed there since the build become the Hub's, once per change, so PLAY
    // neither rebuilds over them nor shows other values than the game uses. The
    // first time the Hub sees a game folder it takes all of its settings.
    private void AdoptGameSettings()
    {
        if (model is null || forkPreview) return; // Previews and self-tests never change saved settings.
        var effective = ForkOutput.ReadEffective(model.OutputDirectory);
        var built = ForkOutput.ReadBuilt(model.OutputDirectory);
        if (effective is null || built is null) return;
        var state = LoadForkState();
        var signature = ForkOutput.Signature(model.OutputDirectory, effective);
        if (state.OutputSignature == signature) return;
        var firstLook = state.OutputSignature.Length == 0;
        var adopted = new List<string>();
        foreach (var option in model.All)
            if (effective.TryGetValue(option.Key, out var id) && built.TryGetValue(option.Key, out var original) && (firstLook || id != original) &&
                option.Selected.Id != id && option.Choices.FirstOrDefault(choice => choice.Id == id) is { } choice)
            {
                option.Selected = choice;
                adopted.Add(option.Label + ": " + choice.Label);
            }
        state.OutputSignature = signature;
        SaveForkState(state);
        if (adopted.Count == 0) return;
        PreGameSettingsBox.IsChecked = model.SkipPreGame;
        PreGameWarning.Visibility = model.SkipPreGame ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
        try { model.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        StatusLabel.Text = (firstLook ? "Using the settings of the game in " + model.OutputDirectory + ": " : "Kept the settings chosen before playing: ") + string.Join(", ", adopted) + ".";
    }

    private ForkState LoadForkState()
    {
        try { return JsonSerializer.Deserialize<ForkState>(File.ReadAllText(Path.Combine(model!.DataDirectory, "fork-state.json")), LauncherModel.Json) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private void SaveForkState(ForkState state)
    {
        try
        {
            Directory.CreateDirectory(model!.DataDirectory);
            File.WriteAllText(Path.Combine(model.DataDirectory, "fork-state.json"), JsonSerializer.Serialize(state, LauncherModel.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await Play(rebuild: false, start: true);
    private async void Rebuild_Click(object sender, RoutedEventArgs e) => await Play(rebuild: true, start: false);

    private async Task Play(bool rebuild, bool start)
    {
        if (model is null || launching) return;
        if (GameRunning || ForkOutput.IsRunning(model.OutputFile)) { StatusLabel.Text = "Close " + model.GameTitle + " first."; return; }
        SetForkBusy(true);
        try
        {
            var runtime = ForkOutput.RuntimeStamp(model.ProjectRoot);
            var reason = rebuild ? "" : ForkOutput.StaleReason(model.Values(), model.GameFolder, model.OutputDirectory, runtime);
            if (reason is not null)
            {
                StatusLabel.Text = (reason + " Building the game…").TrimStart();
                await BuildInPlace(runtime);
            }
            if (start)
            {
                StatusLabel.Text = "Starting " + model.GameTitle + "…";
                StartGame();
            }
            else StatusLabel.Text = "Build completed.";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally
        {
            SetForkBusy(false);
            await RefreshBackend(false);
            if (GameRunning) StatusLabel.Text = model.GameTitle + " is running.";
        }
    }

    // The backend's own build, into an empty sibling folder that then replaces the old one.
    private async Task BuildInPlace(string runtime)
    {
        var output = model!.OutputDirectory;
        var next = ForkOutput.NextFolder(output);
        ForkOutput.RemoveLeftover(next);
        Directory.CreateDirectory(model.DataDirectory);
        var id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        var request = Path.Combine(model.DataDirectory, id + "-request.json");
        var built = Path.Combine(next, model.OutputExecutableName);
        var command = model.PrepareBuild(request, built);
        command.ArgumentList.Remove("--desktop-shortcut"); // It would point at the sibling folder; made after the swap instead.
        using var process = Process.Start(command) ?? throw new IOException("Could not start the PC backend.");
        var progressPath = request + ".progress.json";
        var progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        progressTimer.Tick += (_, _) =>
        {
            try
            {
                if (!File.Exists(progressPath) || new FileInfo(progressPath).Length > 65536) return;
                var progress = BuildProgress.Parse(File.ReadAllText(progressPath));
                StatusLabel.Text = progress.Label; BusyBar.IsIndeterminate = false; BusyBar.Value = progress.Percent;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException) { }
        };
        progressTimer.Start();
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var result = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(model.DataDirectory, id + "-backend.log"), result + "\n" + error);
            if (process.ExitCode != 0) throw new IOException(LauncherModel.DescribeBackendError(result, error, process.ExitCode));
            using var document = JsonDocument.Parse(result);
            if (!document.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True || !File.Exists(built))
                throw new IOException("The backend did not confirm the EXE build.");
        }
        catch { ForkOutput.RemoveLeftover(next); throw; }
        finally { progressTimer.Stop(); BusyBar.IsIndeterminate = true; BusyBar.Value = 0; }
        ForkOutput.WriteStamp(next, runtime);
        ForkOutput.Carry(output, next, runtime);
        StatusLabel.Text = "Replacing the previous build…";
        ForkOutput.Swap(next, output);
        if (ForkOutput.ReadEffective(output) is { } effective) SaveForkState(new() { OutputSignature = ForkOutput.Signature(output, effective) });
        if (model.CreateDesktopShortcut) CreateGameShortcut();
    }

    // Upstream's optional RaceOne5 shortcut, made by its own script; an existing one is kept.
    private void CreateGameShortcut()
    {
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RaceOne5.lnk");
        if (File.Exists(link)) return;
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(model!.ProjectRoot, "tools", "fm5_create_shortcut.ps1"),
                                         "-TargetPath", model.OutputFile, "-LinkName", "RaceOne5", "-WorkingDirectory", model.OutputDirectory })
            info.ArgumentList.Add(argument);
        try { Process.Start(info)?.WaitForExit(30000); } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
    }

    private void StartGame()
    {
        forkGame?.Dispose();
        forkGame = Process.Start(new ProcessStartInfo(model!.OutputFile) { WorkingDirectory = model.OutputDirectory, UseShellExecute = true });
        forkGameTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        forkGameTimer.Tick -= GameTimer_Tick; forkGameTimer.Tick += GameTimer_Tick;
        forkGameTimer.Start();
        UpdatePlayButton();
    }

    private void GameTimer_Tick(object? sender, EventArgs e)
    {
        if (GameRunning) return;
        forkGameTimer?.Stop();
        if (model is null) return;
        StatusLabel.Text = "Ready to play.";
        AdoptGameSettings();
        UpdatePlayButton();
    }

    private void SetForkBusy(bool busy)
    {
        launching = busy;
        GameBrowseButton.IsEnabled = OutputBrowseButton.IsEnabled = DesktopShortcutBox.IsEnabled = Fm5Tab.IsEnabled = UpdateButton.IsEnabled = !busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) BuildButton.IsEnabled = false;
        UpdatePlayButton();
    }

    private async Task CheckForkUpdates()
    {
        if (model is null) return;
        try { forkUpdate = await ForkUpdates.CheckAsync(model.ProjectRoot); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { forkUpdate = null; }
        ShowForkUpdate();
    }

    private void ShowForkUpdate()
    {
        var info = forkUpdate;
        UpdateCard.Visibility = info is { Branch: "main", Behind: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (info is null) { ForkInfo.Text = ""; return; }
        UpdateTitle.Text = info.Behind == 1 ? "Update available: 1 new change" : $"Update available: {info.Behind} new changes";
        WhatsNewText.Text = string.Join("\n", info.Commits.Select(subject => "•  " + subject));
        var state = info.Branch != "main" ? $"on branch {info.Branch}" : !info.Fetched ? "not checked (offline)" : info.Behind == 0 ? "up to date" : $"{info.Behind} behind";
        ForkInfo.Text = $"This copy: {info.Fork} at {info.Head}, {state}." +
            (string.IsNullOrEmpty(info.Upstream) ? "" : info.UpstreamNotTaken == 0 ? $" It has every commit of {info.Upstream}." :
             $" {info.Upstream} has {info.UpstreamNotTaken} commit{(info.UpstreamNotTaken == 1 ? "" : "s")} the fork has not taken; it takes them only when asked.");
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        WhatsNewText.Visibility = WhatsNewText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (model is null || launching) return;
        if (GameRunning || ForkOutput.IsRunning(model.OutputFile)) { StatusLabel.Text = "Close " + model.GameTitle + " before updating."; return; }
        SetForkBusy(true);
        StatusLabel.Text = "Updating…";
        var progressPath = Path.Combine(model.DataDirectory, "fork-update-progress.json");
        var progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        progressTimer.Tick += (_, _) => { if (ForkScriptResult.Read(progressPath) is { Done: false, Message.Length: > 0 } step) StatusLabel.Text = "Updating: " + step.Message + "…"; };
        var restarting = false;
        try
        {
            Directory.CreateDirectory(model.DataDirectory);
            File.Delete(progressPath);
            using var process = ForkUpdates.StartUpdate(model.ProjectRoot, progressPath);
            progressTimer.Start();
            await process.WaitForExitAsync();
            progressTimer.Stop();
            var result = ForkScriptResult.Read(progressPath);
            if (result is not { Done: true, Ok: true })
                StatusLabel.Text = "Update failed: " + (result?.Error ?? "exit code " + process.ExitCode) + (result?.Log is { } log ? " Log: " + log : "");
            else if (result.HubChanged)
            {
                StatusLabel.Text = "Restarting Race One Hub…";
                model.Save();
                ForkUpdates.StartHubRestart(model.ProjectRoot, Environment.ProcessId);
                restarting = true;
                Close();
            }
            else
            {
                StatusLabel.Text = "Updated to the fork's latest version.";
                forkUpdate = forkUpdate is null ? null : forkUpdate with { Behind = 0, Commits = [] };
                ShowForkUpdate();
                _ = CheckForkUpdates();
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { StatusLabel.Text = "Update failed: " + ex.Message; }
        finally
        {
            progressTimer.Stop();
            if (!restarting) { SetForkBusy(false); await RefreshBackend(false); }
        }
    }
}
