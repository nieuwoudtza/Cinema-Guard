using System;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Sources;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Tools;
using Cinema_Guard.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Cinema_Guard.Services;

/// <summary>Builds the object graph once for the whole app.</summary>
public static class AppServices
{
    public static IServiceProvider Build()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ToolLocator>();
        services.AddSingleton<PhononService>();
        services.AddSingleton<DockerImagePuller>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ModelManager>();
        services.AddSingleton<SpeechService>();
        services.AddSingleton<CensorPipeline>();
        services.AddSingleton<QueueRunner>();
        services.AddSingleton<SubloaderService>();
        services.AddSingleton<EmbeddedSubtitleExtractor>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ModelsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ToolsViewModel>();

        return services.BuildServiceProvider();
    }
}

/// <summary>Small helpers for dialogs and pickers (WinUI needs an explicit window handle).</summary>
public static class WindowHelper
{
    public static IntPtr GetHandle(Microsoft.UI.Xaml.Window? window) =>
        window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(window);

    public static void SetXamlRoot(ContentDialog dialog, Microsoft.UI.Xaml.Window? window)
    {
        if (window?.Content is not null) dialog.XamlRoot = window.Content.XamlRoot;
    }
}
