using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CinemaGuard.Core.Tests")]

namespace CinemaGuard.Core;

public static class CoreInfo
{
    /// <summary>
    /// The version as people see it ("2026.11.01.1"), without the +commit suffix. publish.ps1 sets it as
    /// InformationalVersion for a release; every dev build of the app numbers itself the same way
    /// (Directory.Build.targets).
    /// </summary>
    public static string Version { get; } = DisplayVersion(ReadVersion());

    private static string ReadVersion()
    {
        // Only the app is numbered per build; this library keeps the fallback, so ask the running exe first.
        var entry = Assembly.GetEntryAssembly();
        var source = entry?.GetCustomAttribute<AssemblyProductAttribute>()?.Product == "Cinema Guard" ? entry : typeof(CoreInfo).Assembly;

        var version = source.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>
    /// Velopack only takes 3-part versions, so a release dated 2026.11.01, iteration 1, is packaged as
    /// 2026.1101.1 (year.MMdd.iteration). This turns that back into "2026.11.01.1"; anything else is
    /// returned unchanged.
    /// </summary>
    public static string DisplayVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;

        var parts = version.Split('.');
        if (parts.Length != 3 || parts[0].Length != 4 ||
            !int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var monthDay) || !int.TryParse(parts[2], out var iteration))
            return version;

        var month = monthDay / 100;
        var day = monthDay % 100;
        if (year < 2000 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || iteration < 1)
            return version;

        return $"{year:0000}.{month:00}.{day:00}.{iteration}";
    }
}
