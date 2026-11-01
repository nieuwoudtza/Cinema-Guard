using System;
using System.Threading;
using System.Threading.Tasks;
using Cinema_Guard.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cinema_Guard.ViewModels;

/// <summary>The "update ready" bar: downloaded quietly, installed on restart or when the window closes.</summary>
public sealed partial class MainViewModel
{
    private readonly UpdateService _updates;

    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private string _updateMessage = string.Empty;

    public bool CanRestartToUpdate => IsUpdateReady && !IsBusy;

    partial void OnIsUpdateReadyChanged(bool value) => OnPropertyChanged(nameof(CanRestartToUpdate));

    partial void OnIsRunningChanged(bool value) => OnBusyChanged();

    partial void OnIsSubtitleTaskRunningChanged(bool value) => OnBusyChanged();

    private void OnBusyChanged()
    {
        OnPropertyChanged(nameof(CanRestartToUpdate));
        if (IsUpdateReady) UpdateMessage = DescribeUpdate();
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

    public void RestartToUpdate()
    {
        if (CanRestartToUpdate) _updates.RestartAndApply();
    }

    /// <summary>Called when the window closes: a ready update installs after the app exits.</summary>
    public void ApplyUpdateOnExit() => _updates.ApplyOnExit();

    private string DescribeUpdate() => IsBusy
        ? $"Version {_updates.ReadyVersion} is ready. It installs when the files being processed are done and you restart, or when you close Cinema Guard."
        : $"Version {_updates.ReadyVersion} is ready. Restart to install it, or it installs when you close Cinema Guard.";
}
