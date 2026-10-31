using CinemaGuard.Core.Media;
using CinemaGuard.Core.Settings;

namespace CinemaGuard.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void DefaultsMatchTheOldApp()
    {
        var settings = AppSettings.CreateDefault();

        Assert.Contains("JESUS", settings.WordsFull);
        Assert.Contains("CHRIST", settings.WordsFull);
        Assert.Contains("FUCK", settings.WordsSingle);
        Assert.Contains("MOTHERFUCKER", settings.WordsSingle);
        Assert.DoesNotContain("FUCK", settings.WordsFull);
        Assert.Equal(SubtitleOutputType.Dual, settings.SubtitleOutput);
        Assert.True(settings.CreateSubtitles);
        Assert.True(settings.EmbedSubtitles);
        Assert.Equal("_CG", settings.CustomAffix);
    }

    [Fact]
    public void AssociatedWordsComeFromTheOldTable()
    {
        Assert.Contains("FUCKED-UP", FilterCatalog.Associated("FUCK"));
        Assert.Contains("MOTHERFUCKING", FilterCatalog.Associated("MOTHERFUCKER"));
        Assert.Contains("CUNTS", FilterCatalog.Associated("CUNT"));
        Assert.Equal(["GOD"], FilterCatalog.Associated("GOD"));
    }

    [Fact]
    public void FilterFamiliesToggleEveryAssociatedWord()
    {
        var words = new List<string>();

        FilterCatalog.EnableFamily(words, "SHIT", true);
        Assert.Contains("SHITTIEST", words);

        FilterCatalog.EnableFamily(words, "SHIT", false);
        Assert.DoesNotContain("SHIT", words);
        Assert.Empty(words);
    }

    [Fact]
    public void NormaliseRepairsBadValues()
    {
        var settings = new AppSettings
        {
            CustomAffix = "  ",
            CensorCharacter = "ab",
            WordsFull = [" fuck ", "FUCK", ""],
            MaxCps = 1000,
            EstimatedPadBeforeMs = -5,
            PreferredLanguage = "Detect",
        };

        settings.Normalise();

        Assert.Equal("_CG", settings.CustomAffix);
        Assert.Equal("*", settings.CensorCharacter);
        Assert.Equal(["FUCK"], settings.WordsFull);
        Assert.Equal(45, settings.MaxCps);
        Assert.Equal(0, settings.EstimatedPadBeforeMs);
        Assert.Equal("eng", settings.PreferredLanguage);
    }

    [Fact]
    public void JsonRoundTripKeepsEnumsAsNames()
    {
        var settings = AppSettings.CreateDefault();
        settings.OutputContainer = OutputContainer.Mkv;
        settings.PhononRuntime = PhononRuntimeMode.Local;

        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppSettings.JsonOptions);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!;

        Assert.Contains("\"Mkv\"", json);
        Assert.Contains("\"Local\"", json);
        Assert.Equal(OutputContainer.Mkv, loaded.OutputContainer);
        Assert.Equal(settings.WordsFull, loaded.WordsFull);
    }

    [Fact]
    public void ValidateRejectsAnEmptyFilterAndAnEmptyAffix()
    {
        var settings = new AppSettings { CustomAffix = "", WordsFull = [], WordsSingle = [] };

        var problems = settings.Validate();

        Assert.Contains(problems, p => p.Contains("affix", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(problems, p => p.Contains("filter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SettingsServiceWritesAndReadsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cg-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "settings.json");
            var service = new SettingsService(path);
            service.Current.CustomAffix = "_TEST";
            service.Current.WordsSingle.Add("BLEEP");
            service.Save();

            var reloaded = new SettingsService(path);

            Assert.Equal("_TEST", reloaded.Current.CustomAffix);
            Assert.Contains("BLEEP", reloaded.Current.WordsSingle);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class OutputPathPolicyTests
{
    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cg-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void BuildsTheOutputNextToTheInput()
    {
        var directory = TempDirectory();

        try
        {
            var input = Path.Combine(directory, "Movie.mkv");
            var settings = AppSettings.CreateDefault();

            var plan = OutputPathPolicy.Decide(input, settings, null);

            Assert.Null(plan.Error);
            Assert.False(plan.Skip);
            Assert.Equal(Path.Combine(directory, "Movie_CG.mp4"), plan.OutputPath);
            Assert.Equal(Path.Combine(directory, "Movie_CG.partial.mp4"), plan.PartialPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ForcesMatroskaForTrueHd()
    {
        var directory = TempDirectory();

        try
        {
            var input = Path.Combine(directory, "Movie.mkv");
            var audio = new MediaStreamInfo { Index = 1, CodecType = "audio", CodecName = "truehd", Channels = 8 };

            var plan = OutputPathPolicy.Decide(input, AppSettings.CreateDefault(), audio);

            Assert.EndsWith(".mkv", plan.OutputPath);
            Assert.Equal("mkv", plan.Extension);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SkipsFilesThatAlreadyCarryTheAffix()
    {
        var directory = TempDirectory();

        try
        {
            var input = Path.Combine(directory, "Movie_CG.mkv");
            var settings = AppSettings.CreateDefault();

            var plan = OutputPathPolicy.Decide(input, settings, null);

            Assert.True(plan.Skip);
            Assert.Equal("already processed", plan.SkipReason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CanBeToldToReprocess()
    {
        var directory = TempDirectory();

        try
        {
            var settings = AppSettings.CreateDefault();
            settings.SkipAlreadyProcessed = false;

            var plan = OutputPathPolicy.Decide(Path.Combine(directory, "Movie_CG.mkv"), settings, null);

            Assert.False(plan.Skip);
            Assert.Equal(Path.Combine(directory, "Movie_CG_CG.mp4"), plan.OutputPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NeverProducesTheInputPath()
    {
        var directory = TempDirectory();

        try
        {
            var input = Path.Combine(directory, "Movie.mkv");
            var plan = OutputPathPolicy.Decide(input, AppSettings.CreateDefault(), null);

            Assert.False(string.Equals(plan.OutputPath, input, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecognisesSupportedFileTypes()
    {
        Assert.True(FileTypes.IsSupportedVideo(".MKV"));
        Assert.True(FileTypes.IsSupportedVideo(".mp4"));
        Assert.False(FileTypes.IsSupportedVideo(".srt"));
        Assert.True(FileTypes.IsSupportedSubtitle(".srt"));
    }
}
