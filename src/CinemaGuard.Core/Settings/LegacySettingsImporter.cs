using System.Text.Json;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Settings;

/// <summary>
/// One-time import of the old app's cg.dat. .NET 10 cannot read BinaryFormatter data, so a
/// Windows PowerShell 5.1 script does the deserialisation and hands back JSON.
/// </summary>
public static class LegacySettingsImporter
{
    public sealed record ImportResult(AppSettings? Settings, string? Error, string? Summary);

    public static async Task<ImportResult> ImportAsync(
        IProcessRunner runner,
        string scriptPath,
        string oldExecutable,
        string cgDatPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(scriptPath))
            return new ImportResult(null, "The import script is missing (tools\\Import-CgDat.ps1).", null);

        if (!File.Exists(cgDatPath))
            return new ImportResult(null, $"The settings file was not found: {cgDatPath}", null);

        if (!File.Exists(oldExecutable))
            return new ImportResult(null, $"The old Cinema Guard.exe was not found: {oldExecutable}", null);

        var output = Path.Combine(Path.GetTempPath(), $"cg-legacy-{Guid.NewGuid():N}.json");

        try
        {
            var runnerResult = await runner.RunAsync(
                new ProcessSpec("powershell.exe",
                [
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-File", scriptPath,
                    "-OldExe", oldExecutable,
                    "-CgDat", cgDatPath,
                    "-OutJson", output,
                ]),
                cancellationToken: cancellationToken);

            if (runnerResult.Cancelled) throw new OperationCanceledException(cancellationToken);

            if (!runnerResult.Success || !File.Exists(output))
                return new ImportResult(null, Clean(runnerResult.StdErr) ?? "The import failed.", null);

            return Parse(output);
        }
        catch (Exception ex)
        {
            return new ImportResult(null, ex.Message, null);
        }
        finally
        {
            try
            {
                if (File.Exists(output)) File.Delete(output);
            }
            catch
            {
                // A temp file we cannot delete is not worth failing the import over.
            }
        }
    }

    private static ImportResult Parse(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var settings = AppSettings.CreateDefault();

            if (GetString(root, "OutputType") is { } container)
                settings.OutputContainer = container.Equals("mkv", StringComparison.OrdinalIgnoreCase)
                    ? OutputContainer.Mkv
                    : OutputContainer.Mp4;

            if (GetString(root, "CustomAffix") is { Length: > 0 } affix) settings.CustomAffix = affix;
            if (GetBool(root, "AspectRatio") is { } aspect) settings.AspectRatio = aspect;
            if (GetInt(root, "AspectRatioWidth") is { } width) settings.AspectRatioWidth = width;
            if (GetInt(root, "AspectRatioHeight") is { } height) settings.AspectRatioHeight = height;

            if (GetBool(root, "CreateSubtitles") is { } create) settings.CreateSubtitles = create;
            if (GetBool(root, "EmbedSubtitles") is { } embed) settings.EmbedSubtitles = embed;

            if (GetString(root, "SubtitlesType") is { } type && Enum.TryParse<SubtitleOutputType>(type, ignoreCase: true, out var subtitleType))
                settings.SubtitleOutput = subtitleType;

            var wordsFull = GetList(root, "WordsFull");
            var wordsSingle = GetList(root, "WordsSingle");
            if (wordsFull.Count > 0) settings.WordsFull = wordsFull;
            if (wordsSingle.Count > 0) settings.WordsSingle = wordsSingle;

            if (GetInt(root, "StandardSingleWordBefore") is { } estimatedBefore) settings.EstimatedPadBeforeMs = estimatedBefore;
            if (GetInt(root, "StandardSingleWordAfter") is { } estimatedAfter) settings.EstimatedPadAfterMs = estimatedAfter;
            if (GetInt(root, "OneWordSingleWordBefore") is { } wordBefore) settings.WordPadBeforeMs = wordBefore;
            if (GetInt(root, "OneWordSingleWordAfter") is { } wordAfter) settings.WordPadAfterMs = wordAfter;

            if (GetString(root, "CensorCharacter") is { Length: 1 } censor) settings.CensorCharacter = censor;
            settings.PreferredLanguage = LanguageNames.ToIso(GetString(root, "Language"));

            if (GetBool(root, "MuteVocalsOnly") is { } vocals) settings.MuteVocalsOnly = vocals;
            if (GetBool(root, "ForceSameAudioEncoding") is { } same) settings.ForceSameAudioEncoding = same;
            if (GetBool(root, "KeepOriginalAudio") is { } keep) settings.KeepOriginalAudio = keep;
            if (GetBool(root, "SkipMediaNoSwearing") is { } skip) settings.SkipMediaNoSwearing = skip;
            if (GetBool(root, "DeleteOriginalFiles") is { } delete) settings.DeleteOriginalFiles = delete;
            if (GetBool(root, "AlwaysGenerateSubtitles") is { } generate) settings.GenerateSubtitlesForNewFiles = generate;
            if (GetBool(root, "AlwaysOverwriteSubtitles") is { } overwrite) settings.AlwaysOverwriteSubtitles = overwrite;

            settings.Normalise();

            var summary = $"{settings.WordsFull.Count + settings.WordsSingle.Count} filtered words, " +
                          $"{settings.SubtitleOutput} subtitles, {settings.OutputContainer} output, " +
                          $"language {settings.PreferredLanguage}.";

            return new ImportResult(settings, null, summary);
        }
        catch (Exception ex)
        {
            return new ImportResult(null, $"Could not read the imported settings: {ex.Message}", null);
        }
    }

    private static string? Clean(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Replace("\r\n", " ");

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static List<string> GetList(JsonElement root, string name)
    {
        var list = new List<string>();

        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                list.Add(text.Trim().ToUpperInvariant());
        }

        return list;
    }
}
