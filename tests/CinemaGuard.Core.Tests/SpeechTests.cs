using System.IO.Compression;
using System.Text.Json;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Tests;

public class SpeechTests
{
    [Fact]
    public void CatalogueIdsAndFilesAreConsistent()
    {
        Assert.Equal(SpeechCatalog.Models.Count, SpeechCatalog.Models.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var model in SpeechCatalog.Models)
        {
            Assert.NotEmpty(model.Variants);
            Assert.Equal(model.Variants.Count, model.Variants.Select(v => v.Id).Distinct().Count());

            foreach (var variant in model.Variants)
            {
                Assert.True(variant.DiskSize > 0, $"{model.Id}/{variant.Id} has no size");

                foreach (var file in variant.Files)
                {
                    Assert.StartsWith("https://", file.Url);
                    Assert.True(file.Size > 0);
                    if (file.Sha256 is not null) Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
                }
            }

            Assert.Equal(model.Family == SpeechFamily.Phonon, model.Phonon is not null);
        }

        Assert.NotNull(SpeechCatalog.Find("PHONON-2.5-EN"));
        Assert.True(SpeechCatalog.IsKnownSelection(SpeechCatalog.PairId));
        Assert.True(SpeechCatalog.IsKnownSelection(SpeechCatalog.RemoteId));
        Assert.False(SpeechCatalog.IsKnownSelection("whisper-huge"));
    }

    [Fact]
    public void EnginePacksPointAtVerifiedDownloads()
    {
        foreach (var pack in EnginePacks.All)
        {
            Assert.StartsWith(Path.Combine(SpeechPaths.Root, "engines"), pack.Folder);
            Assert.True(pack.DownloadSize > 0);

            foreach (var source in pack.Sources.Where(s => s.Size > 0 && s.Format != PackFormat.TarBz2))
                Assert.Matches("^[0-9a-f]{64}$", source.Sha256 ?? string.Empty);
        }
    }

    [Fact]
    public void OldSettingsMigrateToTheSpeechModel()
    {
        var withPass = JsonSerializer.Deserialize<AppSettings>("""{ "SchemaVersion": 2, "RunMultilingualPass": true }""", AppSettings.JsonOptions)!;
        withPass.Normalise();
        Assert.Equal(SpeechCatalog.PairId, withPass.SpeechModel);
        Assert.Equal(AppSettings.CurrentSchemaVersion, withPass.SchemaVersion);

        var englishOnly = JsonSerializer.Deserialize<AppSettings>("""{ "SchemaVersion": 2, "RunMultilingualPass": false, "PhononRuntime": "Docker" }""", AppSettings.JsonOptions)!;
        englishOnly.Normalise();
        Assert.Equal(SpeechCatalog.PhononEnglishId, englishOnly.SpeechModel);
        Assert.Equal(PhononRuntimeMode.Docker, englishOnly.PhononRuntime);

        var unknown = new AppSettings { SpeechModel = "something-removed" };
        unknown.Normalise();
        Assert.Equal(SpeechCatalog.PairId, unknown.SpeechModel);
    }

    [Fact]
    public void PlansFollowTheChoice()
    {
        var settings = AppSettings.CreateDefault();
        settings.Hardware = new HardwareInfo { Avx2 = true };

        var pair = SpeechService.Plan(settings);
        Assert.Equal(2, pair.Passes.Count);
        Assert.Equal(SpeechCatalog.PhononEnglishId, pair.Primary.CacheKey);
        Assert.Equal(SpeechCatalog.PhononMultilingualId, pair.Multilingual!.CacheKey);

        settings.SpeechModel = "whisper-small";
        settings.SpeechVariants["whisper-small"] = "en-q8_0";
        var whisper = SpeechService.Plan(settings);
        Assert.Single(whisper.Passes);
        Assert.Equal("whisper-small/en-q8_0", whisper.Primary.CacheKey);
        Assert.True(whisper.Primary.Variant!.EnglishOnly);

        settings.SpeechModel = SpeechCatalog.RemoteId;
        settings.RemoteUrl = "http://gpu-box:8000/";
        settings.RemoteModel = "phonon-2.5-en";
        Assert.Equal("remote:http://gpu-box:8000:phonon-2.5-en", SpeechService.Plan(settings).Primary.CacheKey);
    }

    [Fact]
    public void ParakeetAlwaysRunsOnTheCpu()
    {
        // The GPU pack was slower than the CPU on the test machine and cost 3.6 GB.
        var settings = AppSettings.CreateDefault();
        settings.SpeechModel = "parakeet-tdt-0.6b-v3";
        settings.SpeechUseGpu["parakeet-tdt-0.6b-v3"] = true;

        settings.Hardware = Nvidia(13050, 8, 9);
        var pass = SpeechService.Plan(settings).Primary;

        Assert.False(pass.UseGpu);
        Assert.Contains(EnginePacks.ParakeetCuda13, EnginePacks.Legacy);
        Assert.Contains(EnginePacks.ParakeetCuda12, EnginePacks.Legacy);
    }

    [Theory]
    [InlineData(13050, 8, 9, WhisperRuntime.Cuda13)]
    [InlineData(13000, 6, 1, WhisperRuntime.Cuda12)] // Pascal: CUDA 13 dropped it
    [InlineData(12080, 8, 6, WhisperRuntime.Cuda12)]
    [InlineData(12020, 8, 6, WhisperRuntime.Vulkan)] // driver too old for the CUDA 12 build
    public void WhisperPicksTheBestRuntime(int driver, int major, int minor, WhisperRuntime expected)
    {
        var hardware = Nvidia(driver, major, minor);
        Assert.Equal(expected, hardware.BestWhisperRuntime);
    }

    [Fact]
    public void WhisperFallsBackToTheCpu()
    {
        Assert.Equal(WhisperRuntime.Vulkan, new HardwareInfo { Avx2 = true, HasVulkan = true, Adapters = ["AMD Radeon RX 7800 XT"] }.BestWhisperRuntime);
        Assert.Equal(WhisperRuntime.Cpu, new HardwareInfo { Avx2 = true, HasVulkan = true, Adapters = ["Microsoft Basic Display Adapter"] }.BestWhisperRuntime);
        Assert.Equal(WhisperRuntime.CpuNoAvx, new HardwareInfo { Avx2 = false }.BestWhisperRuntime);
        Assert.Null(EnginePacks.WhisperFor(new HardwareInfo { Avx2 = true }));
        Assert.Same(EnginePacks.WhisperCuda13, EnginePacks.WhisperFor(Nvidia(13050, 8, 9)));
    }

    [Fact]
    public void TranscriptsSplitIntoSentences()
    {
        var words = new List<PhononWord>
        {
            W(0.0, 0.3, "Jesus,"), W(0.35, 0.6, "what"), W(0.65, 0.9, "was"), W(0.95, 1.2, "that?"),
            W(1.3, 1.5, "Get"), W(1.55, 1.8, "down."),
            W(3.5, 3.8, "Run"), // after a long pause
        };

        var result = TranscriptShaper.Shape(words, 10);

        Assert.Equal(3, result.Segments.Count);
        Assert.Equal("Jesus, what was that?", result.Segments[0].Text);
        Assert.Equal(0.0, result.Segments[0].Start);
        Assert.Equal(1.2, result.Segments[0].End);
        Assert.Equal("Get down.", result.Segments[1].Text);
        Assert.Equal("Run", result.Segments[2].Text);
        Assert.Equal(10, result.DurationSeconds);
    }

    [Fact]
    public void LongRunsOfSpeechAreCapped()
    {
        var words = Enumerable.Range(0, 60).Select(i => W(i * 0.3, i * 0.3 + 0.25, "word")).ToList();
        var result = TranscriptShaper.Shape(words, 20);

        Assert.All(result.Segments, s => Assert.True(s.End - s.Start <= TranscriptShaper.MaxSegmentSeconds + 0.5));
        Assert.Equal(60, result.Segments.Sum(s => s.Text.Split(' ').Length));
    }

    [Fact]
    public void AudioIsCutInTheQuietestPlace()
    {
        const int rate = 1000;
        var samples = Enumerable.Range(0, 70 * rate).Select(i => 0.5f).ToArray();

        // Silence from 22.0 to 22.6 s: the first cut must land inside it, not at the 26 s limit.
        for (var i = 22_000; i < 22_600; i++) samples[i] = 0;

        var pieces = WaveFile.SplitAtQuietPoints(samples, rate, 26, 8);

        Assert.InRange(pieces[0].Length, 22_000, 22_600);
        Assert.Equal(samples.Length, pieces.Sum(p => p.Length));
        Assert.All(pieces, p => Assert.True(p.Length <= 26 * rate));
        for (var i = 1; i < pieces.Count; i++) Assert.Equal(pieces[i - 1].Start + pieces[i - 1].Length, pieces[i].Start);
    }

    [Fact]
    public void ArchivesOnlyUnpackTheWantedFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var zip = Path.Combine(folder, "pack.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var name in new[] { "build/win-x64/whisper.dll", "build/win-x64/sub/ggml.dll", "build/other.dll", "bin/x64/cublas64_13.dll", "readme.md" })
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(name);
                }
            }

            var byPrefix = Path.Combine(folder, "prefix");
            Assert.Equal(2, Downloader.Extract(zip, new PackSource("x", 1, null, PackFormat.Zip, EntryPrefix: "build/win-x64/"), byPrefix, CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(byPrefix, "whisper.dll")));
            Assert.True(File.Exists(Path.Combine(byPrefix, "sub", "ggml.dll")));

            var byName = Path.Combine(folder, "names");
            Assert.Equal(1, Downloader.Extract(zip, new PackSource("x", 1, null, PackFormat.Zip, Names: ["cublas64_13.dll"]), byName, CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(byName, "cublas64_13.dll")));

            var evil = Path.Combine(folder, "evil.zip");
            using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("build/win-x64/../../../escaped.dll").Open());
                writer.Write("x");
            }

            Assert.Throws<DownloadException>(() =>
                Downloader.Extract(evil, new PackSource("x", 1, null, PackFormat.Zip, EntryPrefix: "build/win-x64/"), Path.Combine(folder, "evil"), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void PhononKeepsItsCacheNames()
    {
        var item = new MediaItem(@"C:\films\Film.mkv");

        Assert.Equal(item.EnglishCachePath, item.CachePathFor(SpeechCatalog.PhononEnglishId));
        Assert.Equal(item.MultilingualCachePath, item.CachePathFor(SpeechCatalog.PhononMultilingualId));
        Assert.Equal(Path.Combine(item.CacheFolder, "transcript.json"), item.CachePathFor("whisper-small/q8_0"));
    }

    [Fact]
    public void TranscriptsLiveInTheCacheNotBesideTheVideo()
    {
        var item = new MediaItem(@"C:\films\Film.mkv");

        Assert.StartsWith(MediaItem.CacheRoot, item.CacheFolder);
        Assert.StartsWith("Film-", Path.GetFileName(item.CacheFolder));
        Assert.All(
            new[] { item.GeneratedPath, item.EnglishCachePath, item.MultilingualCachePath, item.TranscriptCachePath, item.LanguageReportPath },
            path => Assert.Equal(item.CacheFolder, Path.GetDirectoryName(path)));

        // Another film of the same name elsewhere gets its own folder.
        Assert.NotEqual(item.CacheFolder, new MediaItem(@"D:\other\Film.mkv").CacheFolder);
    }

    [Fact]
    public void LegacyFilesBesideTheVideoMoveIntoTheCache()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cg-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var item = new MediaItem(Path.Combine(folder, "film.mkv"));

        try
        {
            File.WriteAllText(Path.Combine(folder, "film.phonon-en.json"), "{}");
            File.WriteAllText(Path.Combine(folder, "film.generated.srt"), "1");

            item.MoveLegacyFiles();

            Assert.False(File.Exists(Path.Combine(folder, "film.phonon-en.json")));
            Assert.False(File.Exists(Path.Combine(folder, "film.generated.srt")));
            Assert.True(File.Exists(item.EnglishCachePath));
            Assert.True(File.Exists(item.GeneratedPath));
        }
        finally
        {
            item.DeleteCache();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TranscriptCacheCanIgnoreTheModel()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var film = Path.Combine(folder, "film.mkv");
            File.WriteAllText(film, "x");
            var cache = Path.Combine(folder, "film.transcript.json");

            var source = TranscriptSource.ForFile(film, "whisper-small/q8_0", 1, DownmixMode.Plain);
            TranscriptCache.Save(cache, source, new PhononResult { Text = "hello" });

            var other = TranscriptSource.ForFile(film, "parakeet-tdt-0.6b-v2/int8", 1, DownmixMode.Plain);
            Assert.Null(TranscriptCache.Load(cache, other));
            Assert.Equal("hello", TranscriptCache.Load(cache, other, anyModel: true)?.Text);

            var otherTrack = TranscriptSource.ForFile(film, "whisper-small/q8_0", 2, DownmixMode.Plain);
            Assert.Null(TranscriptCache.Load(cache, otherTrack, anyModel: true));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://box:8000", "http://box:8000/v1/audio/transcriptions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/audio/transcriptions")]
    [InlineData("https://api.groq.com/openai/v1/audio/transcriptions", "https://api.groq.com/openai/v1/audio/transcriptions")]
    public void ServerAddressesBecomeTheTranscriptionEndpoint(string typed, string expected)
    {
        Assert.Equal(expected, new RemoteSpec(typed, "m", null, 0, null).Endpoint);
    }

    [Fact]
    public void ApiKeysAreStoredEncrypted()
    {
        var stored = RemoteTranscriber.Protect("sk-secret");
        Assert.NotNull(stored);
        Assert.DoesNotContain("sk-secret", stored);
        Assert.Equal("sk-secret", RemoteTranscriber.Unprotect(stored));
        Assert.Null(RemoteTranscriber.Unprotect("not base64 at all"));
        Assert.Null(RemoteTranscriber.Protect("  "));
    }

    private static HardwareInfo Nvidia(int driver, int major, int minor) => new()
    {
        NvidiaName = "NVIDIA Test",
        CudaDriverVersion = driver,
        ComputeMajor = major,
        ComputeMinor = minor,
        Avx2 = true,
        HasVulkan = true,
        Adapters = ["NVIDIA Test"],
    };

    private static PhononWord W(double start, double end, string text) => new() { Start = start, End = end, Text = text };

    [Fact]
    public void UntickedGenerateReusesATranscriptFromAnotherModel()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cg-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var film = Path.Combine(folder, "film.mkv");
            File.WriteAllText(film, "x");

            var settings = AppSettings.CreateDefault();
            settings.SpeechModel = SpeechCatalog.PhononEnglishId;

            var item = new MediaItem(film) { GenerateSubtitles = false };
            var audio = new AudioSelection(new MediaStreamInfo { Index = 1, CodecType = "audio", CodecName = "aac" }, 0, 0);

            // A transcript from a different engine, written where non-Phonon models cache themselves.
            var source = TranscriptSource.ForFile(film, "whisper-small/q8_0", 0, settings.DialogueDownmix);
            TranscriptCache.Save(item.TranscriptCachePath, source, new PhononResult
            {
                Text = "hello there",
                Segments = [new PhononSegment { Start = 0, End = 1, Text = "hello there" }],
            });

            var (english, multilingual) = CensorPipeline.LoadCachedTranscripts(item, settings, audio);

            Assert.NotNull(english);
            Assert.Equal("hello there", english!.Text);
            Assert.Null(multilingual);
        }
        finally
        {
            new MediaItem(Path.Combine(folder, "film.mkv")).DeleteCache();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void FindsTheDownloadedFfmpegBeforeThePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "cg-tools-" + Guid.NewGuid().ToString("N"));
        var previousRoot = SpeechPaths.Root;

        try
        {
            SpeechPaths.Root = root;
            Directory.CreateDirectory(Path.GetDirectoryName(ToolDownloader.FfmpegPath)!);
            File.WriteAllText(ToolDownloader.FfmpegPath, "not a real exe");
            File.WriteAllText(ToolDownloader.FfprobePath, "not a real exe");

            var locator = new ToolLocator(new ProcessRunner());

            Assert.Equal(ToolDownloader.FfmpegPath, locator.ResolveFfmpeg(new AppSettings()));
            Assert.Equal(ToolDownloader.FfprobePath, locator.ResolveFfprobe(new AppSettings()));
        }
        finally
        {
            SpeechPaths.Root = previousRoot;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA\n", "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba")]
    [InlineData("60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba  ffmpeg-release-essentials.zip", "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba")]
    [InlineData("<html>Not found</html>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadsTheFfmpegChecksumFile(string? text, string? expected) =>
        Assert.Equal(expected, ToolDownloader.ParseSha256(text));

    [Fact]
    public void SweepsLeftoverArchivesOnceNoInstallIsRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), "cg-sweep-" + Guid.NewGuid().ToString("N"));
        var previousRoot = SpeechPaths.Root;

        try
        {
            SpeechPaths.Root = root;
            Directory.CreateDirectory(ModelManager.DownloadsFolder);

            var manager = new ModelManager(new SettingsService(Path.Combine(root, "settings.json")), new ProcessRunner());

            var oldFile = Path.Combine(ModelManager.DownloadsFolder, "old.zip");
            var freshFile = Path.Combine(ModelManager.DownloadsFolder, "fresh.zip");
            File.WriteAllText(oldFile, "x");
            File.WriteAllText(freshFile, "x");
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-2));

            manager.SweepDownloads(TimeSpan.FromHours(1));

            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(freshFile));
        }
        finally
        {
            SpeechPaths.Root = previousRoot;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
