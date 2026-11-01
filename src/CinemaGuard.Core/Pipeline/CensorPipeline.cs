using System.Diagnostics;
using CinemaGuard.Core.Ffmpeg;
using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Sources;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Subtitles;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Pipeline;

public sealed class PipelineException(string message) : Exception(message);

/// <summary>
/// One file, end to end. Every step is cancellable, every failure is reported on the item, and the
/// user's input and subtitle files are never touched (except the explicit recycle option, after validation).
/// </summary>
public sealed class CensorPipeline(IProcessRunner runner, ToolLocator locator, SpeechService speech)
{
    private readonly EmbeddedSubtitleExtractor _extractor = new(runner);

    public event Action<MediaItem>? ItemChanged;

    public async Task RunAsync(MediaItem item, AppSettings settings, CancellationToken cancellationToken)
    {
        item.Status = MediaStatus.Running;
        item.Progress = 0;
        item.Message = null;
        item.OutputPath = null;
        item.FallbackNote = null;

        try
        {
            await ExecuteAsync(item, settings, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            item.Status = MediaStatus.Cancelled;
            item.StatusText = "Cancelled";
            item.Message = "Cancelled";
            Log.Info($"{Path.GetFileName(item.InputPath)}: cancelled");
        }
        catch (PipelineException ex)
        {
            Fail(item, ex.Message);
        }
        catch (Exception ex)
        {
            Fail(item, ex.Message);
            Log.Error($"Unexpected failure on {item.InputPath}", ex);
        }
        finally
        {
            Raise(item);
        }
    }

    private async Task ExecuteAsync(MediaItem item, AppSettings settings, CancellationToken cancellationToken)
    {
        SetStatus(item, "Checking the file");

        if (!File.Exists(item.InputPath))
            throw new PipelineException("The input file no longer exists.");

        if (!FileTypes.IsSupportedVideo(Path.GetExtension(item.InputPath)))
            throw new PipelineException($"Unsupported file type '{Path.GetExtension(item.InputPath)}'.");

        EnsureReadable(item.InputPath);

        var ffmpeg = locator.ResolveFfmpeg(settings)
                     ?? throw new PipelineException("FFmpeg is not installed. Open Tools and press Set up to install it.");
        var ffprobe = locator.ResolveFfprobe(settings)
                      ?? throw new PipelineException("FFprobe is not installed. Open Tools and press Set up to install it.");

        SetStatus(item, "Reading media information");
        var probeResult = await runner.RunAsync(new ProcessSpec(ffprobe, FfmpegCommands.Probe(item.InputPath)),
            cancellationToken: cancellationToken);

        if (probeResult.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (!probeResult.Success || string.IsNullOrWhiteSpace(probeResult.StdOut))
            throw new PipelineException($"FFprobe could not read the file: {Tail(probeResult.StdErr)}");

        var probe = MediaProbeParser.Parse(probeResult.StdOut, item.InputPath);
        item.Probe = probe;

        var audio = StreamSelector.SelectAudio(probe, settings.PreferredLanguage)
                    ?? throw new PipelineException("The file has no audio track.");

        item.Audio = audio;

        var plan = OutputPathPolicy.Decide(item.InputPath, settings, audio.Stream);
        if (plan.Skip)
        {
            Skip(item, plan.SkipReason ?? "already processed");
            return;
        }

        if (plan.Error is not null) throw new PipelineException(plan.Error);

        var outputPath = plan.OutputPath!;
        var partialPath = plan.PartialPath!;

        CheckSpace(item, probe, settings);

        using var work = WorkFolder.Create(settings.KeepWorkFiles);

        SetStatus(item, "Looking for subtitles");
        var (subtitleCues, subtitleSource) = await LoadSubtitlesAsync(item, probe, settings, ffmpeg, work, cancellationToken);
        item.SubtitleSource = subtitleSource;

        PhononResult? english = null;
        PhononResult? multilingual = null;

        if (item.GenerateSubtitles)
        {
            (english, multilingual) = await TranscribeAsync(item, settings, probe, audio, ffmpeg, work, cancellationToken);
        }
        else
        {
            (english, multilingual) = LoadCachedTranscripts(item, settings, audio);
        }

        List<Cue>? legacyOneWord = null;
        if (english is null && File.Exists(item.OneWordPath))
        {
            legacyOneWord = TryReadSubtitleFile(item.OneWordPath);
            if (legacyOneWord is not null && item.SubtitleSource == "None") item.SubtitleSource = "One-word subtitles";
        }

        // Without a transcript, the generated cues are the last source of hits. With one they would
        // only repeat the transcript's own words, so they are not matched a second time.
        if (english is null && legacyOneWord is null && subtitleCues is null && File.Exists(item.GeneratedPath))
        {
            subtitleCues = TryReadSubtitleFile(item.GeneratedPath);
            if (subtitleCues is not null) item.SubtitleSource = "Generated";
        }

        if (english is null && multilingual is null && subtitleCues is null && legacyOneWord is null)
            throw new PipelineException("There are no subtitles and no transcript to work from. Tick Generate for this file or add a subtitle file.");

        // The written/embedded subtitles: the original subtitles, else the generated cues, else the one-word cues.
        var outputCues = subtitleCues;
        if (outputCues is null && english is not null)
        {
            if (File.Exists(item.GeneratedPath)) outputCues = TryReadSubtitleFile(item.GeneratedPath);
            if (outputCues is null && !english.IsEmpty) outputCues = CueBuilder.Build(english, settings.MaxCps);
            if (outputCues is not null && item.SubtitleSource == "None") item.SubtitleSource = "Generated";
        }

        outputCues ??= legacyOneWord;

        SetStatus(item, "Finding filtered words");

        var options = new HitCollectorOptions(
            settings.WordsFull,
            settings.WordsSingle,
            settings.EstimatedPadBeforeMs,
            settings.EstimatedPadAfterMs,
            settings.WordPadBeforeMs,
            settings.WordPadAfterMs);

        var hits = HitCollector.Collect(
            options,
            probe.DurationSeconds,
            audio.OffsetSeconds,
            english,
            multilingual,
            subtitleCues,
            legacyOneWord,
            settings.AlignSubtitleFiles);

        item.Hits = hits;
        item.SyncFit = hits.Fit;

        if (settings.SkipMediaNoSwearing && hits.Ranges.Count == 0)
        {
            Skip(item, "no filtered words");
            return;
        }

        item.Language = ResolveLanguage(audio.Stream, outputCues);

        // Subtitle files this run creates next to the video; removed again if the run is cancelled.
        var createdSubtitles = new List<string>();

        try
        {
            var subtitleInputs = WriteSubtitleFiles(
                item, settings, hits, outputCues, ReferenceEquals(outputCues, subtitleCues), work, createdSubtitles);

            SetStatus(item, "Encoding");

            var ranges = hits.Ranges;
            string? filterScript = null;
            var vocals = false;

            if (ranges.Count > 0)
            {
                vocals = settings.MuteVocalsOnly &&
                         english is not null &&
                         ChannelLayouts.CanSplitVocals(audio.Stream.ChannelLayout);

                if (vocals)
                {
                    var perChannel = await BuildVocalsRangesAsync(
                        item, settings, probe, audio, hits, subtitleCues, english!, ffmpeg, work, cancellationToken);

                    if (perChannel.Count == 0)
                    {
                        vocals = false;
                    }
                    else
                    {
                        filterScript = work.File("vocals.txt");
                        VocalsGraphBuilder.Write(
                            filterScript,
                            audio.Ordinal,
                            audio.Stream.ChannelLayout!,
                            ChannelLayouts.ChannelsFor(audio.Stream.ChannelLayout!)!,
                            perChannel);
                    }
                }

                if (!vocals)
                {
                    filterScript = work.File("mute.txt");
                    MuteFilterWriter.Write(filterScript, audio.Ordinal, ranges);
                }
            }
            else if (settings.MuteVocalsOnly)
            {
                Log.Info($"{item.Stem}: no filtered words, vocals-only mode not needed.");
            }

            var request = new EncodeRequest
            {
                InputPath = item.InputPath,
                OutputPath = partialPath,
                Probe = probe,
                Audio = audio,
                Ranges = ranges,
                FilterScriptPath = filterScript,
                KeepOriginalAudio = settings.KeepOriginalAudio,
                ForceSameAudioEncoding = settings.ForceSameAudioEncoding,
                Container = plan.Extension == "mp4" ? OutputContainer.Mp4 : OutputContainer.Mkv,
                Subtitles = subtitleInputs,
                Language = item.Language,
                AudioTitle = ranges.Count == 0 ? "Original" : vocals ? "Cinema Guard (Vocals Only)" : "Cinema Guard",
                AspectRatio = settings.AspectRatio,
                AspectRatioWidth = settings.AspectRatioWidth,
                AspectRatioHeight = settings.AspectRatioHeight,
            };

            TryDelete(partialPath);

            // The .partial never outlives a failed, cancelled or crashed attempt.
            var moved = false;

            try
            {
                var encodeArguments = FfmpegCommands.Encode(request);
                Log.Info($"{item.Stem}: ffmpeg {CommandLine.Join(encodeArguments)}");

                var progress = new FfmpegProgress(probe.DurationSeconds);
                var encode = await runner.RunAsync(
                    new ProcessSpec(ffmpeg, encodeArguments),
                    onStandardOutput: line =>
                    {
                        progress.Feed(line);
                        if (progress.Percent <= 0) return;
                        item.Progress = progress.Percent;
                        item.StatusText = progress.Speed is null ? "Encoding" : $"Encoding ({progress.Speed})";
                        Raise(item);
                    },
                    cancellationToken: cancellationToken);

                if (encode.Cancelled) throw new OperationCanceledException(cancellationToken);
                if (!encode.Success) throw new PipelineException($"FFmpeg failed: {Tail(encode.StdErr)}");

                if (!File.Exists(partialPath))
                    throw new PipelineException("FFmpeg reported success but wrote no output file.");

                SetStatus(item, "Checking the output");

                var expectedAudioStreams = ranges.Count > 0 && settings.KeepOriginalAudio ? 2 : 1;
                var validation = await OutputValidator.ValidateAsync(
                    runner, ffprobe, partialPath, probe, expectedAudioStreams, subtitleInputs.Count, cancellationToken);

                if (validation.Cancelled) throw new OperationCanceledException(cancellationToken);
                if (!validation.Ok) throw new PipelineException($"The output is not usable: {validation.Error}");

                TryDelete(outputPath);
                File.Move(partialPath, outputPath, overwrite: true);
                moved = true;
            }
            finally
            {
                if (!moved) TryDelete(partialPath);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var path in createdSubtitles) TryDelete(path);
            throw;
        }

        item.OutputPath = outputPath;
        item.Progress = 100;
        item.Status = MediaStatus.Succeeded;
        item.StatusText = "Done";

        var summary = hits.Ranges.Count == 0
            ? "No filtered words were heard; copied without mutes."
            : $"{hits.Hits.Count} filtered word{(hits.Hits.Count == 1 ? string.Empty : "s")} in {hits.Ranges.Count} range{(hits.Ranges.Count == 1 ? string.Empty : "s")}.";

        if (english?.Truncated == true) summary += " The transcript was truncated; some words may be missing.";
        if (item.FallbackNote is not null) summary += " " + item.FallbackNote;

        item.Message = summary;

        if (settings.DeleteOriginalFiles) DeleteOriginals(item);
    }

    private async Task<(PhononResult? English, PhononResult? Multilingual)> TranscribeAsync(
        MediaItem item, AppSettings settings, MediaProbe probe, AudioSelection audio, string ffmpeg, WorkFolder work, CancellationToken cancellationToken)
    {
        var layout = audio.Stream.ChannelLayout ?? string.Empty;
        var plan = SpeechService.Plan(settings);
        var primaryPass = plan.Primary;
        var secondPass = plan.Multilingual;

        var source = TranscriptSource.ForFile(item.InputPath, primaryPass.CacheKey, audio.Ordinal, settings.DialogueDownmix);
        var primaryCache = item.CachePathFor(primaryPass.CacheKey);
        var english = settings.AlwaysOverwriteSubtitles ? null : TranscriptCache.Load(primaryCache, source);

        PhononResult? multilingual = null;
        TranscriptSource? multilingualSource = null;

        if (secondPass is not null)
        {
            multilingualSource = TranscriptSource.ForFile(item.InputPath, secondPass.CacheKey, audio.Ordinal, settings.DialogueDownmix);
            if (!settings.AlwaysOverwriteSubtitles)
                multilingual = TranscriptCache.Load(item.CachePathFor(secondPass.CacheKey), multilingualSource);
        }

        var wav = work.File("audio16k.wav");
        var needsAudio = english is null || (secondPass is not null && multilingual is null);

        if (needsAudio)
        {
            // Say what is missing before spending minutes on the audio.
            foreach (var pass in plan.Passes)
            {
                if (speech.Problem(pass, settings) is { } problem) throw new PipelineException(problem);
            }

            SetStatus(item, "Extracting audio for transcription");
            var extract = await runner.RunAsync(
                new ProcessSpec(ffmpeg, FfmpegCommands.ExtractAudio16k(item.InputPath, audio.Ordinal, settings.DialogueDownmix, layout, wav)),
                cancellationToken: cancellationToken);

            if (extract.Cancelled) throw new OperationCanceledException(cancellationToken);
            if (!extract.Success) throw new PipelineException($"Could not extract the audio: {Tail(extract.StdErr)}");
        }

        if (english is null)
        {
            var label = secondPass is null ? $"Transcribing with {primaryPass.DisplayName}" : "Transcribing (English model)";
            SetStatus(item, label);

            var outcome = await speech.TranscribeAsync(
                primaryPass, wav, settings,
                line => SetStatus(item, $"{label}: {Shorten(line, 80)}"),
                cancellationToken);

            english = outcome.Result;
            TranscriptCache.Save(primaryCache, source, english);

            if (english.Truncated) item.Message = "The transcript was truncated; some words may be missing.";
            RecordPass(item, primaryPass, outcome);
        }

        WriteGenerated(item, english, settings);

        if (secondPass is not null)
        {
            if (multilingual is null)
            {
                SetStatus(item, "Transcribing (multilingual pass)");
                var outcome = await speech.TranscribeAsync(
                    secondPass, wav, settings,
                    line => SetStatus(item, "Transcribing (multilingual pass): " + Shorten(line, 80)),
                    cancellationToken);

                multilingual = outcome.Result;
                TranscriptCache.Save(item.CachePathFor(secondPass.CacheKey), multilingualSource!, multilingual);
                RecordPass(item, secondPass, outcome);
            }

            item.ForeignSpans = ForeignLanguageReporter.Analyse(english, multilingual);
            SaveLanguageReport(item);
        }

        return (english, multilingual);
    }

    /// <summary>
    /// Generate is unticked: use whatever transcripts this audio already has. The selected model's
    /// caches come first; a transcript from another model still beats none.
    /// </summary>
    internal static (PhononResult? English, PhononResult? Multilingual) LoadCachedTranscripts(MediaItem item, AppSettings settings, AudioSelection audio)
    {
        var plan = SpeechService.Plan(settings);

        TranscriptSource SourceFor(string key) => TranscriptSource.ForFile(item.InputPath, key, audio.Ordinal, settings.DialogueDownmix);

        var english = TranscriptCache.Load(item.CachePathFor(plan.Primary.CacheKey), SourceFor(plan.Primary.CacheKey));
        var multilingual = plan.Multilingual is { } second
            ? TranscriptCache.Load(item.CachePathFor(second.CacheKey), SourceFor(second.CacheKey))
            : null;

        if (english is null)
        {
            foreach (var path in new[] { item.EnglishCachePath, item.TranscriptCachePath, item.MultilingualCachePath })
            {
                english = TranscriptCache.Load(path, SourceFor(plan.Primary.CacheKey), anyModel: true);
                if (english is not null) break;
            }
        }

        return (english, multilingual);
    }

    private static void WriteGenerated(MediaItem item, PhononResult english, AppSettings settings)
    {
        if (english.IsEmpty) return;

        try
        {
            var cues = CueBuilder.Build(english, settings.MaxCps);
            if (cues.Count == 0) return;

            SrtWriter.Write(item.GeneratedPath, cues);

            if (settings.WriteOneWordSrt)
            {
                var words = english.Words
                    .Where(w => w.End > w.Start)
                    .Select(w => new Cue { StartSeconds = w.Start, EndSeconds = w.End, Text = w.Text });

                SrtWriter.Write(item.OneWordPath, words);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{item.Stem}: could not write the generated subtitles: {ex.Message}");
        }
    }

    private static void SaveLanguageReport(MediaItem item)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(item.ForeignSpans, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });

            File.WriteAllText(item.LanguageReportPath, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"{item.Stem}: could not write the language report: {ex.Message}");
        }
    }

    private List<SubtitleInput> WriteSubtitleFiles(
        MediaItem item,
        AppSettings settings,
        HitSet hits,
        List<Cue>? sourceCues,
        bool sourceIsSubtitleFile,
        WorkFolder work,
        List<string> createdFiles)
    {
        var inputs = new List<SubtitleInput>();

        if (!settings.CreatesSubtitles || sourceCues is not { Count: > 0 }) return inputs;

        var cues = sourceCues.Select(c => c.Clone()).ToList();

        // The sync fit maps a subtitle file onto the transcript; generated cues are already on it.
        if (sourceIsSubtitleFile && settings.RetimeOutputSubtitles && hits.Fit.Accepted)
        {
            foreach (var cue in cues)
            {
                cue.StartSeconds = hits.Fit.Map(cue.Start.TotalSeconds);
                cue.EndSeconds = hits.Fit.Map(cue.End.TotalSeconds);
            }
        }

        var basePath = settings.EmbedSubtitles
            ? work.File("subtitles")
            : Path.Combine(item.Folder, item.Stem);

        var fresh = settings.EmbedSubtitles
            ? []
            : new[] { CensoredSubtitleWriter.FullPath(basePath, settings), CensoredSubtitleWriter.CensoredPath(basePath, settings) }
                .Where(path => !File.Exists(path))
                .ToList();

        var result = CensoredSubtitleWriter.Write(cues, basePath, settings, settings.SubtitleOutput);

        if (!settings.EmbedSubtitles)
        {
            createdFiles.AddRange(fresh.Where(path => path == result.FullPath || path == result.CensoredPath));
            return inputs;
        }

        if (result.FullPath is not null) inputs.Add(new SubtitleInput(result.FullPath, "Full", item.Language));
        if (result.CensoredPath is not null) inputs.Add(new SubtitleInput(result.CensoredPath, "Censored", item.Language));

        return inputs;
    }

    private async Task<Dictionary<string, IReadOnlyList<TimeRange>>> BuildVocalsRangesAsync(
        MediaItem item,
        AppSettings settings,
        MediaProbe probe,
        AudioSelection audio,
        HitSet hits,
        List<Cue>? subtitleCues,
        PhononResult english,
        string ffmpeg,
        WorkFolder work,
        CancellationToken cancellationToken)
    {
        var perChannel = new Dictionary<string, IReadOnlyList<TimeRange>>(StringComparer.Ordinal);
        var layout = audio.Stream.ChannelLayout ?? string.Empty;
        var channels = ChannelLayouts.ChannelsFor(layout);
        if (channels is null) return perChannel;

        var options = new HitCollectorOptions(
            settings.WordsFull, settings.WordsSingle,
            settings.EstimatedPadBeforeMs, settings.EstimatedPadAfterMs,
            settings.WordPadBeforeMs, settings.WordPadAfterMs);

        List<Hit> wholeCueHits = [];
        if (subtitleCues is { Count: > 0 })
            wholeCueHits = HitCollector.FromSubtitles(subtitleCues, hits.Fit, options, []).Where(h => h.WholeCue).ToList();

        var dialogueChannels = channels.Where(channel => ChannelLayouts.DialogueChannels.Contains(channel)).ToList();
        var index = 0;

        foreach (var channel in dialogueChannels)
        {
            index++;

            if (channel == "FC")
            {
                perChannel[channel] = hits.Ranges;
                continue;
            }

            SetStatus(item, $"Analysing audio channel {index}/{dialogueChannels.Count} ({channel})");

            var channelWav = work.File($"channel-{channel}.wav");
            var extract = await runner.RunAsync(
                new ProcessSpec(ffmpeg, FfmpegCommands.ExtractChannel16k(item.InputPath, audio.Ordinal, channel, 2.5, channelWav)),
                cancellationToken: cancellationToken);

            if (extract.Cancelled) throw new OperationCanceledException(cancellationToken);

            if (!extract.Success)
            {
                Log.Warn($"{item.Stem}: channel {channel} could not be extracted: {Tail(extract.StdErr)}");
                continue;
            }

            PhononResult transcript;

            try
            {
                transcript = (await speech.TranscribeAsync(SpeechService.Plan(settings).Primary, channelWav, settings, null, cancellationToken)).Result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"{item.Stem}: channel {channel} could not be transcribed: {ex.Message}");
                continue;
            }

            var channelHits = HitCollector.FromTranscript(transcript, HitSource.English, options);
            channelHits.AddRange(wholeCueHits);

            var ranges = HitCollector.BuildRanges(channelHits, probe.DurationSeconds, audio.OffsetSeconds);
            if (ranges.Count > 0) perChannel[channel] = ranges;
        }

        return perChannel;
    }

    private async Task<(List<Cue>? Cues, string Source)> LoadSubtitlesAsync(
        MediaItem item, MediaProbe probe, AppSettings settings, string ffmpeg, WorkFolder work, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(item.CustomSubtitlePath) && File.Exists(item.CustomSubtitlePath))
        {
            var cues = TryReadSubtitleFile(item.CustomSubtitlePath);
            if (cues is not null) return (cues, "Selected file");
        }

        if (File.Exists(item.SidecarPath))
        {
            var cues = TryReadSubtitleFile(item.SidecarPath);
            if (cues is not null) return (cues, MediaItem.SubtitleFileSource);
        }

        var stream = StreamSelector.SelectSubtitle(probe, settings.PreferredLanguage, out var reason);

        if (stream is not null)
        {
            var target = work.File("embedded.srt");
            var extracted = await _extractor.ExtractAsync(ffmpeg, probe, stream, target, cancellationToken);
            if (extracted is not null)
            {
                var cues = TryReadSubtitleFile(extracted);
                if (cues is not null) return (cues, "Embedded");
            }
        }
        else if (reason is not null)
        {
            Log.Info($"{item.Stem}: {reason}");
        }

        return (null, "None");
    }

    private static List<Cue>? TryReadSubtitleFile(string path)
    {
        try
        {
            return SubtitleReader.ReadFile(path).Cues;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read the subtitle file {path}", ex);
            return null;
        }
    }

    private static string ResolveLanguage(MediaStreamInfo audio, List<Cue>? cues)
    {
        if (!string.IsNullOrWhiteSpace(audio.Language)) return audio.Language;

        if (cues is { Count: > 0 })
        {
            var sample = string.Join(" ", cues.Take(200).Select(cue => cue.Text));
            var guess = LanguageIdentifier.Identify(sample);

            // The detector speaks ISO 639-1 ("en"); containers want 639-2 ("eng").
            if (guess.IsKnown) return LanguageNames.ToIso(guess.Code);
        }

        return "und";
    }

    private static void CheckSpace(MediaItem item, MediaProbe probe, AppSettings settings)
    {
        var seconds = Math.Max(60, probe.DurationSeconds);
        var workNeeded = (long)(seconds * 32_000 * (settings.MuteVocalsOnly ? 8 : 1) * 1.2) + 200L * 1024 * 1024;

        if (PathGuard.GetFreeSpace(WorkFolder.Root) < workNeeded)
            throw new PipelineException("Not enough free space for the temporary audio. Free some space on C: and try again.");

        var outputFolder = Path.GetDirectoryName(item.InputPath) ?? item.Folder;
        long inputSize = 0;

        try
        {
            inputSize = new FileInfo(item.InputPath).Length;
        }
        catch (Exception ex)
        {
            Log.Warn($"{item.Stem}: could not read the input size for the space check: {ex.Message}");
        }

        if (PathGuard.GetFreeSpace(outputFolder) < (long)(inputSize * 1.1))
            throw new PipelineException("Not enough free space next to the video for the output file.");
    }

    /// <summary>A file another program holds open for writing (a download, a copy) is not ready yet.</summary>
    private static void EnsureReadable(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PipelineException($"The input file cannot be opened (is it still being copied?): {ex.Message}");
        }
    }

    private static void DeleteOriginals(MediaItem item)
    {
        RecycleBin.Send(item.InputPath);
        RecycleBin.Send(item.SidecarPath);
        RecycleBin.Send(item.GeneratedPath);
        RecycleBin.Send(item.OneWordPath);
        RecycleBin.Send(item.EnglishCachePath);
        RecycleBin.Send(item.MultilingualCachePath);
        RecycleBin.Send(item.TranscriptCachePath);
        RecycleBin.Send(item.LanguageReportPath);
    }

    /// <summary>Logs which engine ran the pass; a quiet fall back to the CPU is also said on the row.</summary>
    private static void RecordPass(MediaItem item, SpeechPass pass, SpeechOutcome outcome)
    {
        Log.Info($"{item.Stem}: {pass.DisplayName} pass done with {outcome.EngineName}" +
                 (outcome.Note is not null ? $" (fallback: {outcome.Note})" : string.Empty));

        if (outcome.Note is null) return;
        item.FallbackNote ??= $"Transcribed on the CPU because the GPU was unavailable ({outcome.Note.Trim().TrimEnd('.')}).";
    }

    private void Skip(MediaItem item, string reason)
    {
        item.Status = MediaStatus.Skipped;
        item.StatusText = "Skipped";
        item.Progress = 0;
        item.Message = reason;
        Log.Info($"{Path.GetFileName(item.InputPath)}: skipped ({reason})");
    }

    private void Fail(MediaItem item, string message)
    {
        item.Status = MediaStatus.Failed;
        item.StatusText = "Failed";
        item.Message = message;
        Log.Error($"{Path.GetFileName(item.InputPath)}: {message}");
    }

    private void SetStatus(MediaItem item, string text)
    {
        item.StatusText = text;
        Raise(item);
    }

    private void Raise(MediaItem item)
    {
        try { ItemChanged?.Invoke(item); } catch (Exception ex) { Log.Warn($"Status listener failed: {ex.Message}"); }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }

    private static string Tail(string text) => LocalPhononRunner.Tail(text);

    private static string Shorten(string text, int length)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= length ? trimmed : trimmed[..length] + "…";
    }
}

public sealed record QueueSummary(int Succeeded, int Skipped, int Failed, int Cancelled, TimeSpan Elapsed)
{
    public int Total => Succeeded + Skipped + Failed + Cancelled;
    public bool Fits => Failed == 0 && Cancelled == 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (Succeeded > 0) parts.Add($"{Succeeded} done");
        if (Skipped > 0) parts.Add($"{Skipped} skipped");
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (Cancelled > 0) parts.Add($"{Cancelled} cancelled");
        return parts.Count == 0 ? "Nothing to do" : string.Join(", ", parts);
    }
}

/// <summary>Runs a queue one file at a time. One failure never stops the rest.</summary>
public sealed class QueueRunner(CensorPipeline pipeline, SpeechService speech)
{
    public async Task<QueueSummary> RunAsync(IReadOnlyList<MediaItem> items, AppSettings settings, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        KeepAwake.Start();
        speech.BeginSession();

        try
        {
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    item.Status = MediaStatus.Cancelled;
                    item.StatusText = "Cancelled";
                    item.Message = "Cancelled";
                    continue;
                }

                await pipeline.RunAsync(item, settings, cancellationToken);
            }
        }
        finally
        {
            // The speech engine stays loaded across the queue's files; free its memory and VRAM now.
            speech.EndSession();
            KeepAwake.Stop();
            stopwatch.Stop();
        }

        return new QueueSummary(
            items.Count(i => i.Status == MediaStatus.Succeeded),
            items.Count(i => i.Status == MediaStatus.Skipped),
            items.Count(i => i.Status == MediaStatus.Failed),
            items.Count(i => i.Status == MediaStatus.Cancelled),
            stopwatch.Elapsed);
    }
}
