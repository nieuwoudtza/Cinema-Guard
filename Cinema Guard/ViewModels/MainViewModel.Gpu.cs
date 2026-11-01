using System;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Phonon;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace Cinema_Guard.ViewModels;

/// <summary>
/// The "GPU image needed" bar: shown when Phonon is set to run on the GPU, Docker is running and the
/// image this version is tested with is not downloaded (a first install, or an update that moved the
/// image on). One button downloads it right there; Phonon falls back to the CPU until then.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly PhononService _phonon;
    private readonly DockerImagePuller _puller;

    [ObservableProperty] private bool _isGpuNoticeOpen;
    [ObservableProperty] private string _gpuNoticeTitle = string.Empty;
    [ObservableProperty] private string _gpuNoticeMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _gpuNoticeSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private bool _canDownloadGpuImage;

    /// <summary>Started once the window is up. Quietly does nothing when Docker is closed: the image cannot be checked then.</summary>
    public async Task CheckGpuImageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            if (!_speech.UsesDockerGpu(Settings) || _puller.IsPulling) return;

            var docker = _locator.ResolveDocker(Settings);
            if (docker is null) return;

            var image = Settings.DockerImage;
            var missing = await Task.Run(async () =>
                await _phonon.IsEngineReadyAsync(docker, cancellationToken) &&
                !await _phonon.ImageExistsAsync(docker, image, cancellationToken), cancellationToken);

            if (!missing) return;

            var (_, tag) = DockerImages.Split(image);
            _dispatcher.TryEnqueue(() => ShowGpuNotice(
                "GPU image needed",
                $"Phonon is set to use your graphics card, but its GPU image ({tag}) is not downloaded yet, so it runs on the CPU. " +
                "It is several GB and downloads once. To stay on the CPU, turn Phonon's GPU switch off on the Models page.",
                InfoBarSeverity.Informational,
                canDownload: true));
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check the GPU image: {ex.Message}");
        }
    }

    /// <summary>This bar's own button started the running download, so the bar also reports how it ended.</summary>
    private bool _downloadingFromNotice;

    public async Task DownloadGpuImageAsync()
    {
        CanDownloadGpuImage = false;
        GpuNoticeTitle = "Downloading the GPU image";
        _downloadingFromNotice = true;

        var (ok, message) = await _puller.PullAsync(Settings);
        _downloadingFromNotice = false;

        ShowGpuNotice(
            ok ? "GPU image ready" : "GPU image not downloaded",
            message,
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            canDownload: !ok);
    }

    /// <summary>The download's progress on the bar, wherever it was started (here, the Tools page or setup).</summary>
    private void OnPullerChanged() => _dispatcher.TryEnqueue(() =>
    {
        if (!_puller.IsPulling)
        {
            // Started elsewhere (the Tools page says how it went): the progress bar has done its job.
            if (!_downloadingFromNotice && GpuNoticeTitle == "Downloading the GPU image") IsGpuNoticeOpen = false;
            return;
        }

        if (_puller.Status.Length == 0) return;

        IsGpuNoticeOpen = true;
        GpuNoticeSeverity = InfoBarSeverity.Informational;
        GpuNoticeTitle = "Downloading the GPU image";
        GpuNoticeMessage = _puller.IsIndeterminate ? _puller.Status : $"{_puller.Status} ({_puller.Percent:0}%)";
        CanDownloadGpuImage = false;
    });

    private void ShowGpuNotice(string title, string message, InfoBarSeverity severity, bool canDownload)
    {
        GpuNoticeTitle = title;
        GpuNoticeMessage = message;
        GpuNoticeSeverity = severity;
        CanDownloadGpuImage = canDownload;
        IsGpuNoticeOpen = true;
    }
}
