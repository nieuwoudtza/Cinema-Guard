using System;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core.Logging;
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
    public string? ReadyVersion => _ready?.Version.ToString();

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

    /// <summary>Exits, installs the ready release and starts it again.</summary>
    public void RestartAndApply()
    {
        if (_ready is null) return;

        Log.Info($"Restarting to install {ReadyVersion}.");
        _manager.ApplyUpdatesAndRestart(_ready);
    }

    /// <summary>On close: install the ready release once this process has exited, without relaunching.</summary>
    public void ApplyOnExit()
    {
        if (_ready is null) return;

        try
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not schedule the update: {ex.Message}");
        }
    }
}
