using System;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Pipeline;
using Velopack;

namespace Cinema_Guard.Services;

/// <summary>
/// Velopack updates from the WaveBit release feed. Only an installed copy updates; a copy run from
/// bin\ or a plain folder reports "not installed" and never touches the network.
/// </summary>
public sealed class UpdateService
{
    public const string FeedUrl = "https://wavebit.dev/software/cinemaguard";

    /// <summary>Points a test install at a local Releases folder or a staging URL instead of the live feed.</summary>
    private const string FeedOverrideVariable = "CINEMAGUARD_UPDATE_FEED";

    private readonly UpdateManager _manager = new(Environment.GetEnvironmentVariable(FeedOverrideVariable) is { Length: > 0 } feed ? feed : FeedUrl);
    private VelopackAsset? _ready;

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>The version downloaded and waiting for a restart, if any.</summary>
    public string? ReadyVersion => _ready is null ? null : CinemaGuard.Core.CoreInfo.DisplayVersion(_ready.Version.ToString());

    /// <summary>Checks the feed and downloads a newer release in the background. Never throws.</summary>
    public async Task<string?> PrepareAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled) return null;

        try
        {
            // A release downloaded last time but never applied (the app was killed) is still usable.
            _ready = _manager.UpdatePendingRestart;

            var update = await _manager.CheckForUpdatesAsync();
            if (update is null) return ReadyVersion;

            Log.Info($"Update {update.TargetFullRelease.Version} found; downloading.");
            await _manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken);

            _ready = update.TargetFullRelease;
            Log.Info($"Update {ReadyVersion} is ready.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
        }

        return ReadyVersion;
    }

    /// <summary>A headless run (another program's) is working from the installed folder, so installing must wait.</summary>
    public bool IsHeldByHeadlessRun => HeadlessRuns.AnyActive();

    /// <summary>
    /// Starts the installer and ends this process; the installer then opens the app again, the new version,
    /// or this one when the install failed. Silent: Velopack's own progress window has an OK button that
    /// only hides it, which left the user guessing. The app says what happens instead (see MainViewModel).
    /// False, and nothing happens, when there is nothing to install, a headless run holds the folder, or
    /// the installer did not start.
    /// </summary>
    public bool RestartAndApply()
    {
        if (_ready is null) return false;

        if (IsHeldByHeadlessRun)
        {
            Log.Info($"Update {ReadyVersion} waits: a headless run is in progress.");
            return false;
        }

        Log.Info($"Restarting to install {ReadyVersion}.");

        try
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start the update: {ex.Message}");
            return false;
        }

        // The app the installer opens may take the foreground; without this it opens behind other windows.
        AllowSetForegroundWindow(AnyProcess);

        // At once, as Velopack's ApplyUpdatesAndRestart does: the installer does not wait for this process.
        Environment.Exit(0);
        return true;
    }

    /// <summary>
    /// On close: install the ready release once this process has exited, without relaunching. Left for
    /// the next start while a headless run is working from the installed folder. True when it was scheduled.
    /// </summary>
    public bool ApplyOnExit()
    {
        if (_ready is null) return false;

        if (IsHeldByHeadlessRun)
        {
            Log.Info($"Update {ReadyVersion} waits for the next start: a headless run is in progress.");
            return false;
        }

        try
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not schedule the update: {ex.Message}");
            return false;
        }
    }

    private const int AnyProcess = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
