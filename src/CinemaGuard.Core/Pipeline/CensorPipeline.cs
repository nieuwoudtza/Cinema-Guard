using System.Diagnostics;
using System.Globalization;
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
/// user's input and subtitle files are never touched (except the explicit remove-originals option, after validation).
/// </summary>
public sealed class CensorPipeline(IProcessRunner runner, ToolLocator locator, SpeechService speech)
{
    private readonly EmbeddedSubtitleExtractor _extractor = new(runner);

    public event Action<MediaItem>? ItemChanged;

    public Task RunAsync(MediaItem item, AppSettings settings, CancellationToken cancellationToken) =>
        RunAsync(item, settings, scanOnly: false, cancellationToken);

    /// <param name="scanOnly">
    /// Only find the words (Review words before censoring): the file ends <see cref="MediaStatus.Scanned"/>, its
    /// <see cref="MediaItem.Scan"/> held for the review. Otherwise a file holding a scan is censored from it,
    /// and any other file goes end to end.
    /// </param>
    public async Task RunAsync(MediaItem item, AppSettings settings, bool scanOnly, CancellationToken cancellationToken)
    {
        var scan = scanOnly ? null : item.Scan;
        if (scanOnly) item.Scan = null;

        item.Status = MediaStatus.Running;
        item.Progress = 0;
        item.Message = null;
        item.OutputPath = null;
        if (scan is null) item.FallbackNote = null;
        item.StartedAt = DateTimeOffset.Now;
        item.FinishedAt = null;

        // Transcripts an earlier version left beside the video move into the cache; a run keeps its cache fresh.
        item.MoveLegacyFiles();
        item.TouchCache();

        try
        {
            if (scan is not null) await CensorAsync(item, settings, scan, null, cancellationToken);
            else await ExecuteAsync(item, settings, scanOnly, cancellationToken);
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
            // Finished, failed or stopped: without "Keep transcripts" nothing of this file is left behind. A file
            // waiting for the review keeps its transcripts until it is censored: the window that reviews it may be
            // another process than the -start run that scanned it, and finds its words again from them.
            if (!settings.KeepTranscripts && item.Status != MediaStatus.Scanned) item.DeleteCache();

            item.FinishedAt = DateTimeOffset.Now;
            Raise(item);
        }
    }

    private async Task ExecuteAsync(MediaItem item, AppSettings settings, bool scanOnly, CancellationToken cancellationToken)
    {
        var fileProgress = new FileProgress();
        var scan = await FindWordsAsync(item, settings, fileProgress, planCensoring: !scanOnly, cancellationToken);
        if (scan is null) return;

        if (scanOnly)
        {
            HoldScan(item, scan);
            return;
        }

        await CensorAsync(item, settings, scan, fileProgress, cancellationToken);
    }

    /// <summary>
    /// Everything up to the words found: probe, subtitles, transcripts, hits. Null when the file is skipped.
    /// </summary>
    /// <param name="planCensoring">The file is censored straight after, so its progress includes the encode.</param>
    private async Task<ScanResult?> FindWordsAsync(
        MediaItem item, AppSettings settings, FileProgress fileProgress, bool planCensoring, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        SetStatus(item, "Checking the file");

        if (!File.Exists(item.InputPath))
            throw new PipelineException("The input file no longer exists.");

        if (!FileTypes.IsSupportedMedia(Path.GetExtension(item.InputPath)))
            throw new PipelineException($"Unsupported file type '{Path.GetExtension(item.InputPath)}'.");

        var audioFile = FileTypes.IsAudioFile(item.InputPath);

        EnsureReadable(item.InputPath);

        // Checked before the transcript: an FFmpeg too old to mute would otherwise fail only after it.
        var (ffmpeg, ffprobe) = await ResolveToolsAsync(settings, cancellationToken);

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

        // The source's other audio tracks, copied unchanged after the censored one when asked for.
        List<MediaStreamInfo> otherAudio = !audioFile && settings.KeepOtherAudioTracks
            ? probe.AudioStreams.Where(stream => stream.Index != audio.Stream.Index).ToList()
            : [];

        var plan = OutputPathPolicy.Decide(item.InputPath, settings, audio.Stream, otherAudio);
        if (plan.Skip)
        {
            Skip(item, plan.SkipReason ?? "already processed");
            return null;
        }

        if (plan.Error is not null) throw new PipelineException(plan.Error);

        CheckSpace(item, probe, settings);

        // The steps that report progress, weighed by the time they are expected to take on this file (its
        // drive, size, resolution, audio; see RunHistory). The transcription steps are planned once it is
        // known which passes are cached.
        var features = StepFeatures.ForMedia(item.InputPath, probe, audio);
        if (planCensoring) PlanCensoring(fileProgress, settings, audio, features);

        ShowExpected(item, fileProgress);

        using var work = WorkFolder.Create(settings.KeepWorkFiles);

        SetStatus(item, "Looking for subtitles");
        var (subtitleCues, subtitleSource, subtitleDetail) = await LoadSubtitlesAsync(item, probe, settings, ffmpeg, work, cancellationToken);
        item.SubtitleSource = subtitleSource;
        item.SubtitleDetail = subtitleDetail;
        item.RunDetails = [];

        PhononResult? english = null;
        PhononResult? multilingual = null;

        if (item.GenerateSubtitles)
        {
            (english, multilingual) = await TranscribeAsync(item, settings, probe, audio, ffmpeg, work, fileProgress, features, cancellationToken);
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
            throw new PipelineException(item.ExtractSubtitles || !HasTextSubtitleTrack(probe)
                ? "There are no subtitles and no transcript to work from. Tick Generate subtitles for this file, or add a subtitle file."
                : "There are no subtitles and no transcript to work from. Tick Extract subtitles (the video has a subtitle track) or Generate subtitles, or add a subtitle file.");

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

        var options = HitCollectorOptions.From(settings);

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

        if (item.SubtitleDetail is null && item.SubtitleSource != "None")
            item.SubtitleDetail = item.SubtitleSource == "Generated" ? "made from the speech model's transcript" : item.SubtitleSource.ToLowerInvariant();

        item.RunDetails.Add("Subtitles: " + (item.SubtitleDetail ?? "none"));
        item.RunDetails.Add("Speech model: " + DescribeTranscript(settings, english, fileProgress.Has(PrimaryPassStep)));
        item.RunDetails.Add("Word matching: " + DescribeMatching(settings, hits, english, subtitleCues));

        foreach (var word in hits.Trimmed ?? [])
            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"{item.Stem}: {word.Source} word \"{word.Text}\" at {word.Start:0.000} s ran {word.End - word.Start:0.000} s, to {word.End:0.000}; cut to end at {word.TrimmedEnd:0.000} ({word.Reason})."));

        if (settings.SkipMediaNoSwearing && hits.Ranges.Count == 0)
        {
            Skip(item, "no filtered words");
            return null;
        }

        var occurrences = WordOccurrences.Group(hits.Hits);
        WordOccurrences.Describe(occurrences, english, subtitleCues, hits.CaptionFit ?? hits.Fit);

        return new ScanResult
        {
            Probe = probe,
            Audio = audio,
            SubtitleCues = subtitleCues,
            OutputCues = outputCues,
            English = english,
            Hits = hits,
            Occurrences = occurrences,
            Transcribed = fileProgress.Has(PrimaryPassStep) || fileProgress.Has(SecondPassStep),
            Elapsed = stopwatch.Elapsed,
            SettingsKey = ScanResult.KeyFor(settings),
            Ffmpeg = ffmpeg,
            Ffprobe = ffprobe,
        };
    }

    /// <summary>A scan for the review: the file waits, its words held on it, until Censor.</summary>
    private static void HoldScan(MediaItem item, ScanResult scan)
    {
        var count = scan.Occurrences.Count;

        scan.Reviewed = count == 0;
        item.Scan = scan;
        item.Progress = 100;
        item.Status = MediaStatus.Scanned;
        item.StatusText = item.StatusLabel;
        item.Message = count == 0
            ? "No filtered words were found, so there is nothing to review."
            : $"{count} word{(count == 1 ? string.Empty : "s")} to review: click the word count, untick any to leave in, then press Censor.";
    }

    /// <summary>
    /// Mutes the words <paramref name="scan"/> found, less those the review left in, writes the subtitles and
    /// encodes, validates the output and moves it into place.
    /// </summary>
    /// <param name="foundProgress">This run's progress when it also found the words; null for a held scan, which plans its own.</param>
    private async Task CensorAsync(
        MediaItem item, AppSettings settings, ScanResult scan, FileProgress? foundProgress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var probe = scan.Probe;
        var audio = scan.Audio;
        var english = scan.English;
        var subtitleCues = scan.SubtitleCues;
        var outputCues = scan.OutputCues;
        var audioFile = FileTypes.IsAudioFile(item.InputPath);
        var features = StepFeatures.ForMedia(item.InputPath, probe, audio);
        var ffmpeg = scan.Ffmpeg;
        var ffprobe = scan.Ffprobe;
        var fileProgress = foundProgress ?? new FileProgress();

        if (foundProgress is null)
        {
            // The words were found in an earlier run: the file and the tools are checked again, as time has passed.
            SetStatus(item, "Checking the file");

            if (!File.Exists(item.InputPath))
                throw new PipelineException("The input file no longer exists.");

            EnsureReadable(item.InputPath);
            CheckSpace(item, probe, settings);
            (ffmpeg, ffprobe) = await ResolveToolsAsync(settings, cancellationToken);

            PlanCensoring(fileProgress, settings, audio, features);
            ShowExpected(item, fileProgress);
        }

        var otherAudio = !audioFile && settings.KeepOtherAudioTracks
            ? probe.AudioStreams.Where(stream => stream.Index != audio.Stream.Index).ToList()
            : [];

        var plan = OutputPathPolicy.Decide(item.InputPath, settings, audio.Stream, otherAudio);
        if (plan.Skip)
        {
            Skip(item, plan.SkipReason ?? "already processed");
            item.Scan = null;
            return;
        }

        if (plan.Error is not null) throw new PipelineException(plan.Error);

        var outputPath = plan.OutputPath!;
        var partialPath = plan.PartialPath!;

        var vocalsChannels = settings.MuteVocalsOnly ? VocalsChannels(audio) : [];
        var centreRead = vocalsChannels.Count > 0 && ChannelLayouts.HasFrontCenter(audio.Stream.ChannelLayout);

        void DropVocalsChannels()
        {
            if (centreRead) fileProgress.Drop(ChannelAudioStep("FC"));

            foreach (var channel in vocalsChannels)
            {
                fileProgress.Drop(ChannelAudioStep(channel));
                fileProgress.Drop(ChannelStep(channel));
            }

            ShowProgress(item, fileProgress);
        }

        // The words the review unticked are left in: their hits go, and the mute ranges are rebuilt without them.
        var hits = WordOccurrences.LeaveIn(scan.Hits, scan.Occurrences, probe.DurationSeconds, audio.OffsetSeconds);
        var leftIn = scan.Occurrences.Where(occurrence => !occurrence.Mute).ToList();
        item.Hits = hits;

        // A held scan censored again after a failed attempt says these once.
        item.RunDetails.RemoveAll(line => line.StartsWith("Left in: ", StringComparison.Ordinal) || line.StartsWith("Audio: ", StringComparison.Ordinal));

        if (leftIn.Count > 0)
        {
            var listed = string.Join(", ", leftIn.Take(5).Select(occurrence => $"{FormatTime(occurrence.Start)} {occurrence.Label}"));
            item.RunDetails.Add($"Left in: {leftIn.Count} word{(leftIn.Count == 1 ? string.Empty : "s")} you unticked in the review ({listed}{(leftIn.Count > 5 ? ", …" : string.Empty)})");
            Log.Info($"{item.Stem}: {leftIn.Count} word(s) left in by the review: {string.Join(", ", leftIn.Select(occurrence => $"{FormatTime(occurrence.Start)} {occurrence.Label}"))}.");
        }

        // A word with no length mutes nothing; say so rather than count it as muted.
        var unmuted = hits.Hits.Where(hit => hit.End - hit.Start < 0.02).ToList();
        foreach (var hit in unmuted)
            Log.Warn(string.Create(CultureInfo.InvariantCulture, $"{item.Stem}: \"{hit.Label}\" at {hit.Start:0.000} s ({hit.Source}) has no length and is not muted."));

        if (settings.SkipMediaNoSwearing && hits.Ranges.Count == 0)
        {
            Skip(item, leftIn.Count > 0 ? "every word was left in" : "no filtered words");
            item.Scan = null;
            return;
        }

        using var work = WorkFolder.Create(settings.KeepWorkFiles);

        item.Language = ResolveLanguage(audio.Stream, outputCues);

        // Subtitle files this run creates next to the video; removed again if the run is cancelled.
        var createdSubtitles = new List<string>();

        var container = plan.Extension == "mp4" ? OutputContainer.Mp4 : OutputContainer.Mkv;
        var notes = new List<string>();

        try
        {
            // An audio file carries no subtitle tracks; its transcript stays in the cache.
            var subtitleInputs = audioFile
                ? []
                : WriteSubtitleFiles(item, settings, hits, scan.Occurrences, outputCues, ReferenceEquals(outputCues, subtitleCues), work, createdSubtitles);

            List<MediaStreamInfo> keptSubtitles = [];

            if (!audioFile)
            {
                var kept = await KeepSourceSubtitlesAsync(item, probe, settings, container, ffmpeg, work, cancellationToken);
                subtitleInputs.AddRange(kept.Forced);
                keptSubtitles = kept.Copied;

                if (kept.LeftOut > 0)
                    notes.Add($"{kept.LeftOut} image subtitle track{(kept.LeftOut == 1 ? " was" : "s were")} left out: MP4 cannot hold them (choose MKV in Settings to keep them).");
            }

            SetStatus(item, "Encoding");

            var ranges = hits.Ranges;
            string? filterScript = null;
            var vocals = false;

            if (ranges.Count > 0)
            {
                vocals = settings.MuteVocalsOnly &&
                         english is not null &&
                         ChannelLayouts.CanSplitVocals(audio.Stream.ChannelLayout);

                if (!vocals) DropVocalsChannels();

                if (vocals)
                {
                    var perChannel = await BuildVocalsRangesAsync(
                        item, settings, probe, audio, hits, scan.Occurrences, subtitleCues, english!, ffmpeg, work, fileProgress, features, cancellationToken);

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
                            perChannel,
                            settings.MuteFadeMs / 1000.0);
                    }
                }

                if (!vocals)
                {
                    filterScript = work.File("mute.txt");
                    MuteFilterWriter.Write(filterScript, audio.Ordinal, ranges, settings.MuteFadeMs / 1000.0);
                }

                var whyWhole = WholeTrackReason(settings, audio, english, vocals);
                if (whyWhole is not null) notes.Add($"Muted the whole track at each word: {whyWhole}.");

                item.RunDetails.Add($"Audio: {DescribeAudio(probe, audio)}; " + (vocals
                    ? "only the channels carrying the words were muted"
                    : "the whole track was muted at each word" + (whyWhole is null ? string.Empty : $" ({whyWhole})")));
            }
            else
            {
                item.RunDetails.Add($"Audio: {DescribeAudio(probe, audio)}; nothing to mute, copied unchanged");
                if (settings.MuteVocalsOnly) Log.Info($"{item.Stem}: no filtered words, vocals-only mode not needed.");

                // Nothing to mute: the audio is copied, not re-encoded.
                DropVocalsChannels();
                fileProgress.Plan(EncodeStep, ExpectedEncodeSeconds(features, reencodesAudio: false));
                ShowExpected(item, fileProgress);
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
                Container = container,
                Subtitles = subtitleInputs,
                ExtraAudio = otherAudio,
                ExtraSubtitles = keptSubtitles,
                AudioFileExtension = audioFile ? OutputPathPolicy.AudioFileExtension(item.InputPath) : null,
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

                var encode = await RunFfmpegStepAsync(
                    item, fileProgress, EncodeStep, ffmpeg, encodeArguments,
                    StepKind.Encode, features with { Reencode = ranges.Count > 0 },
                    encodeProgress => item.StatusText = encodeProgress.Speed is null ? "Encoding" : $"Encoding ({encodeProgress.Speed})",
                    cancellationToken);

                if (encode.Cancelled) throw new OperationCanceledException(cancellationToken);
                if (!encode.Success) throw new PipelineException($"FFmpeg failed: {Tail(encode.StdErr)}");

                if (!File.Exists(partialPath))
                    throw new PipelineException("FFmpeg reported success but wrote no output file.");

                // VLC ignores an MP4 subtitle track's default flag; this is what makes it show the track.
                if (container == OutputContainer.Mp4 && request.ShownSubtitle >= 0)
                {
                    try
                    {
                        if (!Mp4SubtitleFlags.Show(partialPath, request.ShownSubtitle))
                            Log.Warn($"{item.Stem}: the shown subtitle track was not found in the output.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn($"{item.Stem}: could not mark the subtitles to be shown: {ex.Message}");
                    }
                }

                SetStatus(item, "Checking the output");

                var validation = await OutputValidator.ValidateAsync(
                    runner, ffprobe, partialPath, probe, request.ExpectedAudioStreams, request.ExpectedSubtitleStreams,
                    cancellationToken, expectVideo: !audioFile);

                if (validation.Cancelled) throw new OperationCanceledException(cancellationToken);
                if (!validation.Ok) throw new PipelineException($"The output is not usable: {validation.Error}");

                fileProgress.Complete(CheckStep);
                ShowProgress(item, fileProgress);

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
            ? leftIn.Count > 0 ? "Every word was left in; copied without mutes." : "No filtered words were heard; copied without mutes."
            : $"{hits.Hits.Count} filtered word{(hits.Hits.Count == 1 ? string.Empty : "s")} in {hits.Ranges.Count} range{(hits.Ranges.Count == 1 ? string.Empty : "s")}.";

        if (leftIn.Count > 0 && hits.Ranges.Count > 0)
            summary += $" {leftIn.Count} left in by you.";

        if (unmuted.Count > 0)
            summary += $" {unmuted.Count} of them had no length to mute (the log lists them).";

        if (english?.Truncated == true) summary += " The transcript was truncated; some words may be missing.";
        if (item.FallbackNote is not null) summary += " " + item.FallbackNote;
        foreach (var note in notes) summary += " " + note;

        item.Message = summary;
        item.Scan = null;

        // The file's own time: finding the words and censoring them, not the wait for the review between.
        RunHistory.Default.Record(StepKind.File, features with { Transcribed = scan.Transcribed }, scan.Elapsed + stopwatch.Elapsed);

        if (settings.DeleteOriginalFiles) DeleteOriginals(item, settings.DeleteOriginalsPermanently);
    }

    /// <summary>The FFmpeg and FFprobe to run, or why there are none that will do.</summary>
    private async Task<(string Ffmpeg, string Ffprobe)> ResolveToolsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var choice = await locator.FindSupportedFfmpegAsync(settings, cancellationToken);
        var ffmpeg = choice.Path
                     ?? throw new PipelineException("FFmpeg is not installed. Open Tools and press Set up to install it.");
        if (choice.Problem is not null)
            throw new PipelineException($"{choice.Problem} ({ffmpeg}). " + (string.IsNullOrWhiteSpace(settings.FfmpegPath)
                ? "Open Tools and press Set up to install a current copy."
                : "Choose a newer one in Settings, or clear the FFmpeg path there and press Set up on Tools."));
        var ffprobe = locator.ResolveFfprobe(settings, ffmpeg)
                      ?? throw new PipelineException("FFprobe is not installed. Open Tools and press Set up to install it.");

        return (ffmpeg, ffprobe);
    }

    /// <summary>The censoring steps' share of the file's progress: the encode, the check and the vocals channels (dropped if unused).</summary>
    private void PlanCensoring(FileProgress fileProgress, AppSettings settings, AudioSelection audio, StepFeatures features)
    {
        fileProgress.Plan(EncodeStep, ExpectedEncodeSeconds(features, reencodesAudio: true));
        fileProgress.Plan(CheckStep, StepEstimates.CheckOutput);

        var vocalsChannels = settings.MuteVocalsOnly ? VocalsChannels(audio) : [];
        if (vocalsChannels.Count == 0) return;

        var transcribeSeconds = ExpectedTranscribeSeconds(SpeechService.Plan(settings).Primary, settings, features);

        // The centre is only read (to find its words in the other channels), not transcribed again.
        if (ChannelLayouts.HasFrontCenter(audio.Stream.ChannelLayout)) fileProgress.Plan(ChannelAudioStep("FC"), ExpectedExtractSeconds(features));

        foreach (var channel in vocalsChannels)
        {
            fileProgress.Plan(ChannelAudioStep(channel), ExpectedExtractSeconds(features));
            fileProgress.Plan(ChannelStep(channel), transcribeSeconds);
        }
    }

    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss");

    private async Task<(PhononResult? English, PhononResult? Multilingual)> TranscribeAsync(
        MediaItem item, AppSettings settings, MediaProbe probe, AudioSelection audio, string ffmpeg, WorkFolder work,
        FileProgress fileProgress, StepFeatures features, CancellationToken cancellationToken)
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

        if (needsAudio) fileProgress.Plan(AudioStep, ExpectedExtractSeconds(features));
        if (english is null) fileProgress.Plan(PrimaryPassStep, ExpectedTranscribeSeconds(primaryPass, settings, features));
        if (secondPass is not null && multilingual is null)
            fileProgress.Plan(SecondPassStep, ExpectedTranscribeSeconds(secondPass, settings, features));

        ShowExpected(item, fileProgress);

        if (needsAudio)
        {
            // Say what is missing before spending minutes on the audio.
            foreach (var pass in plan.Passes)
            {
                if (speech.Problem(pass, settings) is { } problem) throw new PipelineException(problem);
            }

            SetStatus(item, "Extracting audio for transcription");
            var extract = await RunFfmpegStepAsync(
                item, fileProgress, AudioStep, ffmpeg,
                FfmpegCommands.ExtractAudio16k(item.InputPath, audio.Ordinal, settings.DialogueDownmix, layout, wav),
                StepKind.ExtractAudio, features, null, cancellationToken);

            if (extract.Cancelled) throw new OperationCanceledException(cancellationToken);
            if (!extract.Success) throw new PipelineException($"Could not extract the audio: {Tail(extract.StdErr)}");
        }

        if (english is null)
        {
            var label = secondPass is null ? $"Transcribing with {primaryPass.DisplayName}" : "Transcribing (English model)";
            SetStatus(item, label);

            var outcome = await TranscribeStepAsync(
                item, fileProgress, PrimaryPassStep, primaryPass, wav, features, settings,
                line => ShowEngineLine(item, label, line),
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
                var outcome = await TranscribeStepAsync(
                    item, fileProgress, SecondPassStep, secondPass, wav, features, settings,
                    line => ShowEngineLine(item, "Transcribing (multilingual pass)", line),
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

            item.EnsureCacheFolder();
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

            item.EnsureCacheFolder();
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
        IReadOnlyList<WordOccurrence> occurrences,
        List<Cue>? sourceCues,
        bool sourceIsSubtitleFile,
        WorkFolder work,
        List<string> createdFiles)
    {
        var inputs = new List<SubtitleInput>();

        if (!settings.CreatesSubtitles || sourceCues is not { Count: > 0 }) return inputs;

        var cues = sourceCues.Select(c => c.Clone()).ToList();

        // A word the review left in is left readable in its line, found before the lines are retimed below.
        // A subtitle file is placed on the speech by the fit, as for the review; generated cues are already on it.
        var spared = WordOccurrences.SparedInCues(occurrences, cues, sourceIsSubtitleFile ? hits.CaptionFit ?? hits.Fit : SyncFit.Identity);

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

        var result = CensoredSubtitleWriter.Write(cues, basePath, settings, settings.SubtitleOutput, spared);

        if (!settings.EmbedSubtitles)
        {
            createdFiles.AddRange(fresh.Where(path => path == result.FullPath || path == result.CensoredPath));
            return inputs;
        }

        // The track chosen in Settings is shown when the video opens; when only the other one is written, that one.
        var showFull = settings.ShowSubtitles switch
        {
            ShownSubtitles.Full => result.FullPath is not null,
            ShownSubtitles.Censored => result.CensoredPath is null,
            _ => false,
        };
        var showCensored = settings.ShowSubtitles != ShownSubtitles.Off && !showFull;

        if (result.FullPath is not null) inputs.Add(new SubtitleInput(result.FullPath, "Full", item.Language, Shown: showFull));
        if (result.CensoredPath is not null) inputs.Add(new SubtitleInput(result.CensoredPath, "Censored", item.Language, Shown: showCensored));

        return inputs;
    }

    private sealed record KeptSubtitles(List<SubtitleInput> Forced, List<MediaStreamInfo> Copied, int LeftOut);

    /// <summary>
    /// The source's own subtitle tracks that go into the output. A forced text track is masked like the
    /// others and embedded flagged forced; a forced image track cannot be masked, so it is copied. With
    /// "Keep the other subtitle tracks", the rest are copied unchanged. MP4 cannot hold image tracks:
    /// those are counted as left out.
    /// </summary>
    private async Task<KeptSubtitles> KeepSourceSubtitlesAsync(
        MediaItem item, MediaProbe probe, AppSettings settings, OutputContainer container, string ffmpeg, WorkFolder work,
        CancellationToken cancellationToken)
    {
        var forced = new List<SubtitleInput>();
        var copied = new List<MediaStreamInfo>();
        var leftOut = 0;

        var entries = settings.WordsFull.Concat(settings.WordsSingle).ToList();
        var censor = settings.MaskCharacter();

        foreach (var stream in probe.SubtitleStreams)
        {
            var wanted = stream.IsForced ? settings.KeepForcedSubtitles : settings.KeepOtherSubtitles;
            if (!wanted) continue;

            if (stream.IsForced && stream.IsTextSubtitle)
            {
                var extracted = await _extractor.ExtractAsync(ffmpeg, probe, stream, work.File($"forced-{stream.Index}.srt"), cancellationToken);
                var cues = extracted is null ? null : TryReadSubtitleFile(extracted);

                if (cues is not { Count: > 0 })
                {
                    Log.Warn($"{item.Stem}: the forced subtitle track #{stream.Index} could not be read; it is left out.");
                    continue;
                }

                var masked = cues.Select(cue =>
                {
                    var copy = cue.Clone();
                    copy.Text = CensoredSubtitleWriter.Mask(cue.Text, entries, censor, out _);
                    return copy;
                }).ToList();

                var path = work.File($"forced-{stream.Index}-masked.srt");
                SrtWriter.Write(path, masked);

                forced.Add(new SubtitleInput(
                    path,
                    string.IsNullOrWhiteSpace(stream.Title) ? "Forced" : stream.Title,
                    stream.Language ?? item.Language,
                    Forced: true));
                continue;
            }

            if (FfmpegCommands.CanKeepSubtitle(stream, container)) copied.Add(stream);
            else leftOut++;
        }

        return new KeptSubtitles(forced, copied, leftOut);
    }

    private async Task<Dictionary<string, IReadOnlyList<TimeRange>>> BuildVocalsRangesAsync(
        MediaItem item,
        AppSettings settings,
        MediaProbe probe,
        AudioSelection audio,
        HitSet hits,
        IReadOnlyList<WordOccurrence> occurrences,
        List<Cue>? subtitleCues,
        PhononResult english,
        string ffmpeg,
        WorkFolder work,
        FileProgress fileProgress,
        StepFeatures features,
        CancellationToken cancellationToken)
    {
        var perChannel = new Dictionary<string, IReadOnlyList<TimeRange>>(StringComparer.Ordinal);
        var leftIn = occurrences.Where(occurrence => !occurrence.Mute).ToList();
        var layout = audio.Stream.ChannelLayout ?? string.Empty;
        var channels = ChannelLayouts.ChannelsFor(layout);
        if (channels is null) return perChannel;

        var options = HitCollectorOptions.From(settings);

        List<Hit> wholeCueHits = [];
        if (subtitleCues is { Count: > 0 })
            wholeCueHits = HitCollector.FromSubtitles(subtitleCues, hits.Fit, options, []).Where(h => h.WholeCue).ToList();

        var dialogueChannels = channels.Where(channel => ChannelLayouts.DialogueChannels.Contains(channel)).ToList();

        // The centre is muted wherever the main pass heard a word. Its audio is read to find the other channels'
        // copies of the dialogue; when it cannot be read, nothing is known about those copies and every
        // dialogue channel is muted with the centre.
        var hasCentre = dialogueChannels.Contains("FC");
        var index = 0;
        MonoWavReader? centre = null;

        if (hasCentre)
        {
            perChannel["FC"] = hits.Ranges;
            SetStatus(item, $"Analysing audio channel {++index}/{dialogueChannels.Count} (FC)");
            centre = await ExtractChannelAsync(item, fileProgress, ffmpeg, audio, "FC", work, features, cancellationToken);
        }

        try
        {
            foreach (var channel in dialogueChannels.Where(channel => channel != "FC"))
            {
                SetStatus(item, $"Analysing audio channel {++index}/{dialogueChannels.Count} ({channel})");

                using var reader = hasCentre && centre is null
                    ? null
                    : await ExtractChannelAsync(item, fileProgress, ffmpeg, audio, channel, work, features, cancellationToken);

                if (reader is null)
                {
                    // Not known what this channel carries, so it is muted with the centre.
                    fileProgress.Drop(ChannelAudioStep(channel));
                    fileProgress.Drop(ChannelStep(channel));
                    ShowProgress(item, fileProgress);
                    perChannel[channel] = hits.Ranges;
                    continue;
                }

                // The words the main pass heard that this channel also carries, too quietly for its own
                // transcript but loud enough to hear once the centre is muted.
                var channelHits = centre is null ? [] : CentreSpeechHits(hits.Hits, centre, reader);
                var carried = channelHits.Count;

                try
                {
                    var transcript = (await TranscribeStepAsync(
                        item, fileProgress, ChannelStep(channel), SpeechService.Plan(settings).Primary, reader.Path,
                        features, settings, null, cancellationToken)).Result;

                    var (trimmed, _) = StretchedWords.Trim(transcript, HitSource.English, subtitleCues, hits.CaptionFit ?? hits.Fit);
                    channelHits.AddRange(HitCollector.FromTranscript(trimmed, HitSource.English, options));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The centre's words it carries are still muted; a word only this channel has is not known.
                    Log.Warn($"{item.Stem}: channel {channel} could not be transcribed: {ex.Message}");
                    fileProgress.Complete(ChannelStep(channel));
                    ShowProgress(item, fileProgress);
                }

                channelHits.AddRange(wholeCueHits);

                // This channel's own transcript hears the words the review left in as well; they stay in here too.
                if (leftIn.Count > 0) channelHits.RemoveAll(hit => WordOccurrences.IsLeftIn(hit, leftIn));

                Log.Info($"{item.Stem}: channel {channel} carries the centre's speech at {carried} of {hits.Hits.Count} words.");

                var ranges = HitCollector.BuildRanges(channelHits, probe.DurationSeconds, audio.OffsetSeconds);
                if (ranges.Count > 0) perChannel[channel] = ranges;
            }
        }
        finally
        {
            centre?.Dispose();
        }

        return perChannel;
    }

    /// <summary>One channel as a 16 kHz mono wav, opened for reading; null (and a warning) when it cannot be extracted.</summary>
    private async Task<MonoWavReader?> ExtractChannelAsync(
        MediaItem item, FileProgress fileProgress, string ffmpeg, AudioSelection audio, string channel, WorkFolder work,
        StepFeatures features, CancellationToken cancellationToken)
    {
        var channelWav = work.File($"channel-{channel}.wav");
        var extract = await RunFfmpegStepAsync(
            item, fileProgress, ChannelAudioStep(channel), ffmpeg,
            FfmpegCommands.ExtractChannel16k(item.InputPath, audio.Ordinal, channel, 2.5, channelWav),
            StepKind.ExtractAudio, features, null, cancellationToken);

        if (extract.Cancelled) throw new OperationCanceledException(cancellationToken);

        if (!extract.Success)
        {
            Log.Warn($"{item.Stem}: channel {channel} could not be extracted: {Tail(extract.StdErr)}");
            return null;
        }

        try
        {
            return new MonoWavReader(channelWav);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn($"{item.Stem}: channel {channel} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>The hits whose words <paramref name="channel"/> carries a copy of from the centre (see <see cref="DialogueLeak"/>).</summary>
    internal static List<Hit> CentreSpeechHits(IEnumerable<Hit> hits, MonoWavReader centre, MonoWavReader channel)
    {
        var carried = new List<Hit>();

        foreach (var hit in hits)
        {
            if (hit.End <= hit.Start) continue;

            var centreSamples = centre.Read(hit.Start, hit.End);
            var channelSamples = channel.Read(hit.Start, hit.End);

            if (DialogueLeak.CarriesSpeech(centreSamples, channelSamples, centre.SampleRate)) carried.Add(hit);
        }

        return carried;
    }

    /// <summary>
    /// The subtitles to find words in: the file picked for this film, the track picked for it, the .srt
    /// beside it, then the best track inside it. Detail says which, for the badge and Peek.
    /// </summary>
    private async Task<(List<Cue>? Cues, string Source, string? Detail)> LoadSubtitlesAsync(
        MediaItem item, MediaProbe probe, AppSettings settings, string ffmpeg, WorkFolder work, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(item.CustomSubtitlePath) && File.Exists(item.CustomSubtitlePath))
        {
            var cues = TryReadSubtitleFile(item.CustomSubtitlePath);
            if (cues is not null) return (cues, "Selected file", $"the file you selected, {Path.GetFileName(item.CustomSubtitlePath)}");
        }

        // A track picked for this film beats the .srt beside it; otherwise the .srt comes first.
        var picked = item.ExtractSubtitles && item.SubtitleStreamIndex is not null;

        if (!picked && File.Exists(item.SidecarPath))
        {
            var cues = TryReadSubtitleFile(item.SidecarPath);
            if (cues is not null) return (cues, MediaItem.SubtitleFileSource, $"{Path.GetFileName(item.SidecarPath)} beside the video");
        }

        // Extract subtitles unticked: the track inside the video is left alone.
        if (!item.ExtractSubtitles) return (null, "None", null);

        var stream = StreamSelector.SelectSubtitle(probe, settings.PreferredLanguage, item.SubtitleStreamIndex, out var reason);

        if (stream is not null)
        {
            var target = work.File("embedded.srt");
            var extracted = await _extractor.ExtractAsync(ffmpeg, probe, stream, target, cancellationToken);
            if (extracted is not null)
            {
                var cues = TryReadSubtitleFile(extracted);
                var how = stream.Index == item.SubtitleStreamIndex ? "the one you chose" : "chosen by language";
                if (cues is not null)
                    return (cues, $"Embedded #{probe.SubtitleNumber(stream)}",
                        $"subtitle track {probe.SubtitleNumber(stream)} inside the video ({how}): {stream.Describe()}");
            }
        }
        else if (reason is not null)
        {
            Log.Info($"{item.Stem}: {reason}");
        }

        // The picked track could not be read: the .srt beside the video is still better than nothing.
        if (picked && File.Exists(item.SidecarPath))
        {
            var cues = TryReadSubtitleFile(item.SidecarPath);
            if (cues is not null) return (cues, MediaItem.SubtitleFileSource, $"{Path.GetFileName(item.SidecarPath)} beside the video");
        }

        return (null, "None", null);
    }

    private static bool HasTextSubtitleTrack(MediaProbe probe) => probe.SubtitleStreams.Any(stream => stream.IsTextSubtitle);

    // ---------------------------------------------------------------- what the run worked from, for Peek

    internal static string DescribeTranscript(AppSettings settings, PhononResult? english, bool transcribedNow)
    {
        if (english is null) return "not used (Generate subtitles is off and there is no saved transcript)";

        var names = string.Join(" + ", SpeechService.Plan(settings).Passes.Select(pass => pass.DisplayName));
        return transcribedNow ? $"{names}, transcribed in this run" : $"{names}, the transcript saved from an earlier run";
    }

    internal static string DescribeMatching(AppSettings settings, HitSet hits, PhononResult? english, List<Cue>? subtitleCues)
    {
        if (hits.Alignment is { } alignment)
            return $"{alignment.MatchedWords:N0} of {alignment.SubtitleWords:N0} subtitle words matched to the speech model ({alignment.MatchedShare * 100:0} %); " +
                   "a word only the subtitles have is timed between the spoken words around it";

        if (english is null || subtitleCues is not { Count: > 0 })
            return "not done; it needs both subtitles and the speech model's transcript";

        return settings.AlignSubtitleFiles
            ? "the subtitles did not match the speech (another language or another cut?), so a word only they have is placed by where it sits in its line"
            : "off in the settings";
    }

    internal static string DescribeAudio(MediaProbe probe, AudioSelection audio) =>
        $"track {audio.Ordinal + 1} of {probe.AudioStreams.Count()}, {audio.Stream.Describe()}";

    /// <summary>Why "Mute only the dialogue channels" fell back to the whole track; null when it was off or worked.</summary>
    internal static string? WholeTrackReason(AppSettings settings, AudioSelection audio, PhononResult? english, bool vocals)
    {
        if (!settings.MuteVocalsOnly || vocals) return null;

        var layout = string.IsNullOrWhiteSpace(audio.Stream.ChannelLayout) ? $"{audio.Stream.Channels ?? 0}-channel" : audio.Stream.ChannelLayout;
        if (!ChannelLayouts.CanSplitVocals(audio.Stream.ChannelLayout)) return $"the {layout} track has no separate dialogue channel";
        if (english is null) return "muting only the dialogue channels needs the speech model's transcript";
        return "the dialogue channels could not be read";
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

    private static void DeleteOriginals(MediaItem item, bool permanently)
    {
        foreach (var path in new[] { item.InputPath, item.SidecarPath, item.OneWordPath })
        {
            if (permanently) RecycleBin.DeletePermanently(path);
            else RecycleBin.Send(path);
        }

        // Cinema Guard's own files for this video: nothing left to re-run.
        item.DeleteCache();
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

    // ---------------------------------------------------------------- progress

    private const string AudioStep = "audio";
    private const string PrimaryPassStep = "transcribe";
    private const string SecondPassStep = "transcribe-multilingual";
    private const string EncodeStep = "encode";
    private const string CheckStep = "check";

    private static string ChannelAudioStep(string channel) => $"channel-audio:{channel}";
    private static string ChannelStep(string channel) => $"channel:{channel}";

    /// <summary>The dialogue channels vocals-only mode transcribes one by one (the centre reuses the main transcript).</summary>
    private static IReadOnlyList<string> VocalsChannels(AudioSelection audio)
    {
        var layout = audio.Stream.ChannelLayout;
        if (!ChannelLayouts.CanSplitVocals(layout) || ChannelLayouts.ChannelsFor(layout!) is not { } channels) return [];

        return channels.Where(channel => ChannelLayouts.DialogueChannels.Contains(channel) && channel != "FC").ToList();
    }

    // The expected times: what earlier runs on alike files took, the built-in guesses until there are some.

    private static double ExpectedExtractSeconds(StepFeatures features) =>
        RunHistory.Default.Estimate(StepKind.ExtractAudio, features, StepEstimates.ExtractAudio(features.DurationSeconds));

    private static double ExpectedEncodeSeconds(StepFeatures features, bool reencodesAudio) =>
        RunHistory.Default.Estimate(
            StepKind.Encode,
            features with { Reencode = reencodesAudio },
            StepEstimates.Encode(features.Bytes, features.DurationSeconds, reencodesAudio));

    private double ExpectedTranscribeSeconds(SpeechPass pass, AppSettings settings, StepFeatures features)
    {
        var device = speech.DeviceName(pass, settings);
        var fallback = Math.Max(60, features.DurationSeconds) / RunHistory.DefaultTranscribeSpeed(pass, device);

        return RunHistory.Default.Estimate(StepKind.Transcribe, features with { Engine = RunHistory.EngineKey(pass, device) }, fallback);
    }

    /// <summary>The file's expected time, its weight in the queue's overall percentage.</summary>
    private void ShowExpected(MediaItem item, FileProgress fileProgress)
    {
        item.ExpectedSeconds = Math.Max(1, fileProgress.TotalWeight);
        Raise(item);
    }

    /// <summary>
    /// Runs an FFmpeg command that writes `-progress pipe:1`, filling the step's share as it goes. A
    /// finished step's time is kept in the run history against the file's features.
    /// </summary>
    private async Task<ProcessResult> RunFfmpegStepAsync(
        MediaItem item, FileProgress fileProgress, string step, string ffmpeg, IReadOnlyList<string> arguments,
        StepKind kind, StepFeatures features, Action<FfmpegProgress>? onProgress, CancellationToken cancellationToken)
    {
        var parser = new FfmpegProgress(features.DurationSeconds);
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            new ProcessSpec(ffmpeg, arguments),
            onStandardOutput: line =>
            {
                parser.Feed(line);
                if (parser.Percent <= 0) return;
                onProgress?.Invoke(parser);
                fileProgress.Report(step, parser.Percent / 100);
                ShowProgress(item, fileProgress);
            },
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            RunHistory.Default.Record(kind, features, stopwatch.Elapsed);
            fileProgress.Complete(step);
            ShowProgress(item, fileProgress);
        }

        return result;
    }

    /// <summary>
    /// One transcription pass as a step. Whisper and Parakeet report their own progress; Phonon and the
    /// server do not, so until a real value arrives the step fills by the elapsed time against what this
    /// model took on earlier files (held at 95 % if it runs long). The time is kept for next time.
    /// </summary>
    private async Task<SpeechOutcome> TranscribeStepAsync(
        MediaItem item, FileProgress fileProgress, string step, SpeechPass pass, string wav, StepFeatures features,
        AppSettings settings, Action<string>? status, CancellationToken cancellationToken)
    {
        var device = speech.DeviceName(pass, settings);
        var expected = ExpectedTranscribeSeconds(pass, settings, features);
        var stopwatch = Stopwatch.StartNew();
        var reported = false;

        using var ticker = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var estimate = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ticker.Token);
                    if (reported) continue;
                    fileProgress.Report(step, Math.Min(0.95, stopwatch.Elapsed.TotalSeconds / expected));
                    ShowProgress(item, fileProgress);
                }
            }
            catch (OperationCanceledException)
            {
                // The pass finished or was cancelled.
            }
        }, CancellationToken.None);

        try
        {
            var outcome = await speech.TranscribeAsync(pass, wav, settings, status, cancellationToken, value =>
            {
                reported = true;
                fileProgress.Report(step, value / 100);
                ShowProgress(item, fileProgress);
            });

            // A quiet fall back to the CPU ran on another device than the one the time is kept for.
            if (outcome.Note is null)
                RunHistory.Default.Record(StepKind.Transcribe, features with { Engine = RunHistory.EngineKey(pass, device) }, stopwatch.Elapsed);

            fileProgress.Complete(step);
            ShowProgress(item, fileProgress);
            return outcome;
        }
        finally
        {
            ticker.Cancel();
            await estimate;
        }
    }

    /// <summary>Copies the file's percentage to the row; small changes are not worth a UI update.</summary>
    private void ShowProgress(MediaItem item, FileProgress fileProgress)
    {
        var percent = fileProgress.Percent;
        if (percent - item.Progress < 0.1) return;

        item.Progress = percent;
        Raise(item);
    }

    /// <summary>
    /// A line the speech engine printed, after the step's label. The transcript itself arrives as a JSON
    /// line on the same stream; that is data, not news for the row (or a --progress reader).
    /// </summary>
    private void ShowEngineLine(MediaItem item, string label, string line)
    {
        if (line.TrimStart().StartsWith('{')) return;

        SetStatus(item, $"{label}: {Shorten(line, 80)}");
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

/// <param name="Scanned">Files a scan-only run left waiting for the review.</param>
public sealed record QueueSummary(int Succeeded, int Skipped, int Failed, int Cancelled, TimeSpan Elapsed, int Scanned = 0)
{
    public int Total => Succeeded + Skipped + Failed + Cancelled + Scanned;
    public bool Fits => Failed == 0 && Cancelled == 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (Scanned > 0) parts.Add($"{Scanned} ready for review");
        if (Succeeded > 0) parts.Add($"{Succeeded} done");
        if (Skipped > 0) parts.Add($"{Skipped} skipped");
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (Cancelled > 0) parts.Add($"{Cancelled} cancelled");
        return parts.Count == 0 ? "Nothing to do" : string.Join(", ", parts);
    }
}

/// <summary>
/// Runs a queue one file at a time. One failure never stops the rest. Files can join a running queue,
/// and a file taken off it is cancelled on its own (or never started) while the others carry on.
/// </summary>
public sealed class QueueRunner(CensorPipeline pipeline, SpeechService speech)
{
    private readonly object _gate = new();
    private readonly HashSet<MediaItem> _removed = [];
    private MediaItem? _current;
    private CancellationTokenSource? _currentCancellation;

    public Task<QueueSummary> RunAsync(IReadOnlyList<MediaItem> items, AppSettings settings, CancellationToken cancellationToken, bool scanOnly = false)
    {
        var queue = new Queue<MediaItem>(items);
        return RunAsync(() => queue.TryDequeue(out var item) ? item : null, settings, cancellationToken, scanOnly);
    }

    /// <param name="next">The next file to run, or null when there is none. Asked after every file, so files added mid-run are picked up.</param>
    /// <param name="scanOnly">Only find each file's words, for the review before censoring (see <see cref="CensorPipeline.RunAsync(MediaItem, AppSettings, bool, CancellationToken)"/>).</param>
    public async Task<QueueSummary> RunAsync(Func<MediaItem?> next, AppSettings settings, CancellationToken cancellationToken, bool scanOnly = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var ran = new List<MediaItem>();

        lock (_gate) _removed.Clear();
        KeepAwake.Start();
        speech.BeginSession();

        try
        {
            while (next() is { } item)
            {
                if (IsRemoved(item)) continue;
                ran.Add(item);

                if (cancellationToken.IsCancellationRequested)
                {
                    item.Status = MediaStatus.Cancelled;
                    item.StatusText = "Cancelled";
                    item.Message = "Cancelled";
                    continue;
                }

                using var itemCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_gate)
                {
                    _current = item;
                    _currentCancellation = itemCancellation;
                }

                try
                {
                    await pipeline.RunAsync(item, settings, scanOnly, itemCancellation.Token);
                }
                finally
                {
                    lock (_gate)
                    {
                        _current = null;
                        _currentCancellation = null;
                    }
                }
            }
        }
        finally
        {
            // The speech engine stays loaded across the queue's files; free its memory and VRAM now.
            speech.EndSession();
            KeepAwake.Stop();
            stopwatch.Stop();
        }

        // Files taken off the queue are not part of its outcome.
        var counted = ran.Where(item => !IsRemoved(item)).ToList();

        return new QueueSummary(
            counted.Count(i => i.Status == MediaStatus.Succeeded),
            counted.Count(i => i.Status == MediaStatus.Skipped),
            counted.Count(i => i.Status == MediaStatus.Failed),
            counted.Count(i => i.Status == MediaStatus.Cancelled),
            stopwatch.Elapsed,
            counted.Count(i => i.Status == MediaStatus.Scanned));
    }

    /// <summary>Takes a file off the running queue: cancels it if it is the one running, and never starts it otherwise.</summary>
    public void Remove(MediaItem item)
    {
        CancellationTokenSource? cancellation;

        lock (_gate)
        {
            _removed.Add(item);
            cancellation = ReferenceEquals(_current, item) ? _currentCancellation : null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The file finished in the meantime.
        }
    }

    private bool IsRemoved(MediaItem item)
    {
        lock (_gate) return _removed.Contains(item);
    }
}
