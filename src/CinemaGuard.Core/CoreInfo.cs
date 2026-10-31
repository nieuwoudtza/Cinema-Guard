using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CinemaGuard.Core.Tests")]

namespace CinemaGuard.Core;

public static class CoreInfo
{
    /// <summary>The release version (Directory.Build.props, or -p:Version from publish.ps1), without the +commit suffix.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var version = typeof(CoreInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
