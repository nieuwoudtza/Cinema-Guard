using CinemaGuard.Core.Media;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Subtitles;

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
        Assert.False(settings.DeleteOriginalFiles);
        Assert.False(settings.DeleteOriginalsPermanently);
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
    public void SpellingsSubtitlesUseAreInTheFamilies()
    {
        var settings = AppSettings.CreateDefault();

        var matches = SwearMatcher.Find("Motherfuck! Shit!", settings.WordsSingle);
        Assert.Equal(["MOTHERFUCK"], matches.Select(m => m.Entry));

        Assert.Contains("GODDAMMIT", FilterCatalog.Associated("GODDAMN"));
        Assert.Contains("DIPSHIT", FilterCatalog.Associated("SHIT"));
        Assert.Equal(FilterCatalog.Associated("FUCK").Count, FilterCatalog.Associated("FUCK").Distinct().Count());
    }

    [Fact]
    public void SettingsFromBeforeVersion4GetTheNewSpellings()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{ "SchemaVersion": 3, "WordsFull": ["GODDAMN"], "WordsSingle": ["MOTHERFUCKER", "FUCK", "DIPSHIT"] }""",
            AppSettings.JsonOptions)!;
        old.Normalise();

        Assert.Equal(AppSettings.CurrentSchemaVersion, old.SchemaVersion);
        Assert.Contains("MOTHERFUCK", old.WordsSingle);
        Assert.Contains("MINDFUCK", old.WordsSingle);
        Assert.Contains("GODDAMMIT", old.WordsFull);
        Assert.DoesNotContain("GODDAMMIT", old.WordsSingle);

        // A spelling already on a list stays put, and families without their root word are left alone.
        Assert.DoesNotContain("DIPSHIT", old.WordsFull);
        Assert.DoesNotContain("BATSHIT", old.WordsSingle);

        // Once saved as version 4, a spelling the user took off stays off.
        var current = new AppSettings { WordsSingle = ["MOTHERFUCKER"] };
        current.Normalise();
        Assert.Equal(["MOTHERFUCKER"], current.WordsSingle);
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
            MuteFadeMs = 9000,
            PreferredLanguage = "Detect",
        };

        settings.Normalise();

        Assert.Equal("_CG", settings.CustomAffix);
        Assert.Equal("*", settings.CensorCharacter);
        Assert.Equal(["FUCK"], settings.WordsFull);
        Assert.Equal(45, settings.MaxCps);
        Assert.Equal(0, settings.EstimatedPadBeforeMs);
        Assert.Equal(500, settings.MuteFadeMs);
        Assert.Equal("eng", settings.PreferredLanguage);
    }

    [Theory]
    [InlineData(" ", true)]
    [InlineData("#", true)]
    [InlineData("a", false)]
    [InlineData("7", false)]
    [InlineData("\t", false)]
    [InlineData("\n", false)]
    [InlineData("", false)]
    [InlineData("**", false)]
    public void ACensorCharacterIsOneSymbolOrASpace(string value, bool valid)
    {
        var settings = new AppSettings { CensorCharacter = value };
        settings.Normalise();

        Assert.Equal(valid, AppSettings.IsValidCensorCharacter(value));
        Assert.Equal(valid ? value : "*", settings.CensorCharacter);
    }

    [Fact]
    public void ASpaceMasksWithANonBreakingSpaceSoTheCueSurvives()
    {
        var settings = new AppSettings { CensorCharacter = " " };

        Assert.Equal(' ', settings.MaskCharacter());
        Assert.Equal("    .", CensoredSubtitleWriter.Mask("Shit.", ["SHIT"], settings.MaskCharacter(), out _));
        Assert.Equal("WHAT THE     ", CensoredSubtitleWriter.Mask("WHAT THE HELL", ["HELL"], settings.MaskCharacter(), out _));
    }

    [Fact]
    public void APhraseKeepsOneSpaceBetweenItsWords()
    {
        var settings = new AppSettings { WordsSingle = ["holy  shit", " HOLY\tSHIT ", "HOLY SHIT"] };

        settings.Normalise();

        Assert.Equal(["HOLY SHIT"], settings.WordsSingle);
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

public class VersionTests
{
    [Theory]
    [InlineData("2026.1101.1", "2026.11.01.1")]
    [InlineData("2026.1101.2", "2026.11.01.2")]
    [InlineData("2027.105.12", "2027.01.05.12")]
    [InlineData("2026.1231.3", "2026.12.31.3")]
    [InlineData("2.0.1", "2.0.1")]
    [InlineData("2026.1301.1", "2026.1301.1")]
    [InlineData("2026.230.1", "2026.230.1")]
    [InlineData("2026.11.01.1", "2026.11.01.1")]
    [InlineData("", "")]
    public void ShowsThePackageVersionAsADate(string package, string expected) =>
        Assert.Equal(expected, CoreInfo.DisplayVersion(package));
}
