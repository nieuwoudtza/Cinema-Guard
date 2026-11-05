using System;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core;
using CinemaGuard.Core.Logging;
using Cinema_Guard.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace Cinema_Guard.ViewModels;

/// <summary>
/// The "update ready" bar: downloaded quietly, installed on restart or when the window closes. The
/// install itself shows nothing, so the bar says what is about to happen, and the next start says
/// whether it worked.
/// </summary>
public sealed partial class MainViewModel
{
    private const string ReadyTitle = "Update ready";

    private readonly UpdateService _updates;

    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private string _updateTitle = ReadyTitle;
    [ObservableProperty] private string _updateMessage = string.Empty;

    /// <summary>Restart now was pressed: the app is about to close for the installer.</summary>
    [ObservableProperty] private bool _isInstallingUpdate;

    // The first start after an update: whether it installed.
    [ObservableProperty] private bool _isUpdateResultOpen;
    [ObservableProperty] private string _updateResultTitle = string.Empty;
    [ObservableProperty] private string _updateResultMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _updateResultSeverity = InfoBarSeverity.Success;

    public bool CanRestartToUpdate => IsUpdateReady && !IsBusy && !IsInstallingUpdate;

    partial void OnIsUpdateReadyChanged(bool value) => OnPropertyChanged(nameof(CanRestartToUpdate));

    partial void OnIsInstallingUpdateChanged(bool value) => OnPropertyChanged(nameof(CanRestartToUpdate));

    partial void OnIsRunningChanged(bool value) => OnBusyChanged();

    partial void OnIsSubtitleTaskRunningChanged(bool value) => OnBusyChanged();

    private void OnBusyChanged()
    {
        OnPropertyChanged(nameof(CanRestartToUpdate));
        if (IsUpdateReady && !IsInstallingUpdate) UpdateMessage = DescribeUpdate();
    }

    /// <summary>Started once the window is up; waits a little so the first launch stays snappy.</summary>
    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!_updates.IsInstalled) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var version = await Task.Run(() => _updates.PrepareAsync(cancellationToken), cancellationToken);
        if (version is null) return;

        _dispatcher.TryEnqueue(() =>
        {
            IsUpdateReady = true;
            UpdateMessage = DescribeUpdate();
        });
    }

    /// <summary>
    /// Says what is about to happen, then closes for the installer, which opens the app again. The
    /// pause is for reading: the window is gone for a few seconds while the files are replaced.
    /// </summary>
    public async Task RestartToUpdateAsync()
    {
        if (!CanRestartToUpdate) return;

        if (_updates.IsHeldByHeadlessRun)
        {
            UpdateMessage = HeldByHeadlessRun();
            return;
        }

        var version = _updates.ReadyVersion;
        IsInstallingUpdate = true;
        UpdateTitle = "Installing the update";
        UpdateMessage = $"Cinema Guard closes now and opens again in a few seconds, as version {version}.";

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Files may have been added to the queue and started meanwhile.
        if (!IsBusy)
        {
            // Saved first: the installer may stop this process before anything else runs.
            ExpectUpdate(version);
            if (_updates.RestartAndApply()) return;
            ExpectUpdate(null);
        }

        IsInstallingUpdate = false;
        UpdateTitle = ReadyTitle;
        UpdateMessage = _updates.IsHeldByHeadlessRun ? HeldByHeadlessRun() : DescribeUpdate();
    }

    /// <summary>Called when the window closes: a ready update installs after the app exits.</summary>
    public void ApplyUpdateOnExit()
    {
        if (_updates.ReadyVersion is not { } version) return;

        ExpectUpdate(version);
        if (!_updates.ApplyOnExit()) ExpectUpdate(null);
    }

    /// <summary>
    /// The first start after an update was started: says whether it installed. True when there was one;
    /// the installer opens the app without focus, so the window then comes forward.
    /// </summary>
    public bool ReportUpdateResult()
    {
        var expected = Settings.PendingUpdateVersion;
        if (string.IsNullOrEmpty(expected)) return false;

        ExpectUpdate(null);

        if (expected == CoreInfo.Version)
        {
            UpdateResultTitle = "Updated";
            UpdateResultMessage = $"Cinema Guard is now version {CoreInfo.Version}.";
            UpdateResultSeverity = InfoBarSeverity.Success;
        }
        else
        {
            Log.Warn($"Update {expected} did not install; still {CoreInfo.Version}.");
            UpdateResultTitle = "The update didn't install";
            UpdateResultMessage = $"Version {expected} could not be installed, so this is still version {CoreInfo.Version}. " +
                                  "It is offered again shortly. The details are in Velopack's log under %LocalAppData%\\velopack.";
            UpdateResultSeverity = InfoBarSeverity.Error;
        }

        IsUpdateResultOpen = true;
        return true;
    }

    private void ExpectUpdate(string? version)
    {
        Settings.PendingUpdateVersion = version;
        _settingsService.Save();
    }

    private string HeldByHeadlessRun() =>
        $"Version {_updates.ReadyVersion} is ready. Another program is using Cinema Guard in the background; restart once it is done.";

    private string DescribeUpdate() => IsBusy
        ? $"Version {_updates.ReadyVersion} is ready. It installs when the files being processed are done and you restart, or when you close Cinema Guard."
        : $"Version {_updates.ReadyVersion} is ready. Restart to install it, or it installs when you close Cinema Guard.";
}
