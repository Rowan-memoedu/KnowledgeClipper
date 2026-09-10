using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeClipper.Workflow;
using NAudio.CoreAudioApi;

var root = Path.GetFullPath(args.FirstOrDefault() ?? throw new ArgumentException("Pass the repository path."));
var output = Path.GetFullPath(args.Skip(1).FirstOrDefault() ?? @"D:\Artifacts\KnowledgeClipper\validation\speech.json");
var smoke = args.Contains("--smoke");
var stateOnly = args.Contains("--state-only");
var latencyOnly = args.Contains("--latency");
var audioRoot = Path.Combine(Path.GetDirectoryName(output)!, "speech-audio");
Directory.CreateDirectory(audioRoot);
var report = new SpeechReport { StartedAt = DateTimeOffset.UtcNow, OperatingSystem = Environment.OSVersion.ToString() };
var config = ModeConfigurationLoader.Load(Path.Combine(root, "modes"));
using var speech = new StreamingSpeech(Path.Combine(root, "helpers", "stream_speech.py"), audioRoot);
var settings = new LanguageSettings();
TestEndpoint? testEndpoint = null;
const string english = "A patient gardener noticed a resilient plant beside the old stone wall.";
var exitCode = 0;
try
{
    if (args.Contains("--temporarily-unmute"))
    {
        testEndpoint = new TestEndpoint();
        report.OutputState = testEndpoint.State;
        Console.WriteLine("TEST: temporarily unmuting the default output endpoint; existing volume is preserved and mute will be restored.");
    }
    if (args.Contains("--prepared"))
    {
        speech.Prepare(settings);
        await speech.PreparationReady.WaitAsync(TimeSpan.FromSeconds(10));
    }
    if (latencyOnly)
    {
        var cold = await Complete("latency_cold", Draft(english, settings));
        Require(cold.ValidOutput, "Cold helper produces valid complete audio and real output.");
        speech.Prepare(settings);
        await speech.PreparationReady.WaitAsync(TimeSpan.FromSeconds(10));
        var warm = await Complete("latency_prepared", Draft(english, settings));
        Require(warm.ValidOutput, "A prepared empty helper produces valid complete audio and real output.");
        Require((warm.Times["playerStarted"] - warm.Times["speechStarted"]).TotalMilliseconds <
            (cold.Times["playerStarted"] - cold.Times["speechStarted"]).TotalMilliseconds,
            "Preparation removes dependency import from the request-to-player path.");
        var replayDraft = Draft(english, settings with { AutoVoice = false, Voice = "invalid-voice-must-not-be-synthesized" });
        replayDraft.AudioPath = warm.Draft.AudioPath;
        var replay = await Complete("latency_saved_replay", replayDraft, replay: true);
        Require(replay.ValidOutput && !replay.Times.ContainsKey("firstAudioChunk") && replay.Audio!.Sha256 == warm.Audio!.Sha256,
            "Prepared replay keeps the same complete audio and makes no synthesis request.");

        var stopped = Begin("latency_stop", Draft(string.Join(" ", Enumerable.Repeat(english, 10)), settings));
        await stopped.OutputReady.Task.WaitAsync(TimeSpan.FromSeconds(90));
        var nextId = speech.PreparedProcessId;
        var stopClock = Stopwatch.StartNew();
        speech.Stop();
        var stoppedResult = await Finish(stopped);
        report.StopCompletionMilliseconds = stopClock.Elapsed.TotalMilliseconds;
        Require(stoppedResult.Status == "朗读已停止" && !speech.IsPlaying && nextId != null &&
            speech.PreparedProcessId == nextId && ProcessExists(nextId.Value),
            "Stop releases only current speech and preserves the single empty prepared helper.");
        speech.Prepare(settings with { PythonPath = Path.Combine(audioRoot, "missing-python.exe") });
        Require(!ProcessExists(nextId!.Value) && speech.PreparedProcessId == null,
            "Changing the configured Python path retires the old prepared helper.");
        speech.Prepare(settings);
        await speech.PreparationReady.WaitAsync(TimeSpan.FromSeconds(10));
        var preparedId = speech.PreparedProcessId;
        speech.Dispose();
        Require(preparedId != null && !ProcessExists(preparedId.Value) && speech.PreparedProcessId == null,
            "Dispose reclaims the empty prepared helper.");
    }
    if (!latencyOnly && !stateOnly)
    {
      var baseline = await Complete("english_default", Draft(english, settings));
      Require(baseline.ValidOutput, "English produces decoded audio and nonzero Windows output.");
      if (!smoke)
      {
        var fast = await Complete("english_rate_plus_50", Draft(english, settings with { RatePercent = 50 }));
        Require(fast.ValidOutput && fast.Audio!.DurationSeconds < baseline.Audio!.DurationSeconds * 0.85,
            "The configured faster rate reduces actual decoded duration.");
        var quiet = await Complete("english_volume_minus_60", Draft(english, settings with { VolumePercent = -60 }));
        Require(quiet.ValidOutput && quiet.Audio!.Rms < baseline.Audio!.Rms * 0.7,
            "The configured lower volume reduces actual decoded RMS.");
        var voice = await Complete("english_alternate_voice", Draft(english, settings with { AutoVoice = false, Voice = "en-US-GuyNeural" }));
        Require(voice.ValidOutput && voice.Audio!.Sha256 != baseline.Audio!.Sha256,
            "An alternate explicit voice returns distinct valid audio and Windows output.");
        var chinese = await Complete("chinese_default", Draft("她做事一向严谨，从不忽略细节。", settings));
        Require(chinese.ValidOutput, "Chinese produces decoded audio and nonzero Windows output.");
        var mixed = await Complete("mixed_default", Draft("我们需要保持 open-minded，认真听取不同的意见。", settings));
        Require(mixed.ValidOutput, "Mixed language produces decoded audio and nonzero Windows output.");

        var savedPath = baseline.Draft.AudioPath!;
        var savedTime = File.GetLastWriteTimeUtc(savedPath);
        var countBeforeReplay = Directory.GetFiles(audioRoot, "*.mp3").Length;
        var replayDraft = Draft(english, settings with { AutoVoice = false, Voice = "invalid-voice-must-not-be-synthesized" });
        replayDraft.AudioPath = savedPath;
        var replay = await Complete("saved_audio_replay", replayDraft, replay: true);
        Require(replay.ValidOutput && replay.Draft.AudioPath == savedPath &&
            !replay.Times.ContainsKey("firstAudioChunk") && replay.Audio!.Sha256 == baseline.Audio!.Sha256 &&
            File.GetLastWriteTimeUtc(savedPath) == savedTime && Directory.GetFiles(audioRoot, "*.mp3").Length == countBeforeReplay,
            "Replay uses the complete saved file without synthesis, even with an invalid synthesis voice.");
      }
    }

    if (!latencyOnly && !smoke)
    {
        var partialFilesBefore = Directory.GetFiles(audioRoot, "*.partial").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var interruptedDraft = Draft("An observer error must still release the player's process.", settings);
        var injected = false;
        int? interruptedPlayerId = null;
        Console.WriteLine("START callback_failure_cleanup");
        await speech.SpeakAsync(interruptedDraft, false, () => {
            if (!injected && interruptedDraft.Times.ContainsKey("firstAudioChunk"))
            {
                injected = true;
                throw new InvalidOperationException("Synthetic observer error for process cleanup verification.");
            }
        }, pid => interruptedPlayerId = pid);
        report.InjectedFailure = new(interruptedDraft.SpeechStatus, interruptedDraft.SpeechError, interruptedPlayerId,
            interruptedPlayerId != null && !ProcessExists(interruptedPlayerId.Value), speech.IsPlaying);
        Require(injected && interruptedDraft.SpeechError != null && !speech.IsPlaying &&
            interruptedPlayerId != null && !ProcessExists(interruptedPlayerId.Value),
            "A callback failure closes its own Python/player processes and retains failure status.");
        var longDraft = Draft(string.Join(" ", Enumerable.Repeat(english, 10)), settings);
        var stopped = Begin("stop_during_output", longDraft);
        await stopped.OutputReady.Task.WaitAsync(TimeSpan.FromSeconds(90));
        // This checks cancellation ownership; actual content quality is validated by the separate content check.
        var independentWork = Task.Delay(300).ContinueWith(_ => "independent work remained runnable", TaskScheduler.Default);
        var stopClock = Stopwatch.StartNew();
        speech.Stop();
        var stoppedResult = await Finish(stopped);
        report.StopCompletionMilliseconds = stopClock.Elapsed.TotalMilliseconds;
        Require(stoppedResult.Draft.SpeechStatus == "朗读已停止" && !speech.IsPlaying && await independentWork == "independent work remained runnable",
            "Stopping active speech completes promptly and does not cancel independent work.");

        var oldDraft = Draft(string.Join(" ", Enumerable.Repeat(english, 10)), settings);
        var old = Begin("replaced_old_draft", oldDraft);
        await old.OutputReady.Task.WaitAsync(TimeSpan.FromSeconds(90));
        var newest = await Complete("replacement_new_draft", Draft("The new sentence should be the only active playback.", settings));
        var oldResult = await Finish(old);
        Require(newest.ValidOutput && oldResult.Draft.SpeechStatus == "朗读已停止" && newest.Draft.SpeechStatus == "朗读完成，可重播",
            "A new draft replaces old playback and old completion does not alter the new draft.");

        var same = Draft("The same draft can restart while an earlier request is still connecting.", settings);
        var superseded = Begin("same_draft_superseded", same);
        var latest = Begin("same_draft_latest", same);
        await Finish(superseded, requireFinalStatus: false);
        var sameResult = await Finish(latest);
        Require(sameResult.ValidOutput && same.SpeechStatus == "朗读完成，可重播" && File.Exists(same.AudioPath),
            "A superseded attempt on the same draft cannot overwrite final status or completed audio.");
        Require(!Directory.GetFiles(audioRoot, "*.partial").Except(partialFilesBefore, StringComparer.OrdinalIgnoreCase).Any(),
            "Stopped and superseded attempts leave no new incomplete audio files.");
    }
}
catch (Exception ex)
{
    report.Failure = ex.Message;
    Console.WriteLine("FAIL " + ex.Message);
    speech.Stop();
    exitCode = 1;
}
finally
{
    testEndpoint?.Dispose();
    report.FinishedAt = DateTimeOffset.UtcNow;
    report.Passed = exitCode == 0;
    WorkflowJson.Save(output, report);
    Console.WriteLine("Evidence: " + output);
}
return exitCode;

ClipDraft Draft(string text, LanguageSettings requested)
{
    var material = new CaptureMaterial(text, null, "generated_allowed", null, "synthetic speech validation", 0, DateTimeOffset.UtcNow);
    return new ClipDraft { Original = material, Material = material, Settings = requested, Configuration = config };
}

void Require(bool value, string description)
{
    report.Checks.Add(new(description, value));
    WorkflowJson.Save(output, report);
    if (!value) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
}

bool ProcessExists(int id)
{
    try { using var process = Process.GetProcessById(id); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

SpeechRun Begin(string name, ClipDraft draft, bool replay = false)
{
    Console.WriteLine("START " + name);
    var run = new SpeechRun(name, draft, replay);
    var previousStatus = "";
    run.Task = speech.SpeakAsync(draft, replay, () => {
        if (draft.SpeechStatus != previousStatus)
        {
            previousStatus = draft.SpeechStatus;
            Console.WriteLine(name + ": " + previousStatus);
        }
        if (draft.Times.ContainsKey("audioOutputStarted")) run.OutputReady.TrySetResult();
    }, pid => {
        run.PlayerId = pid;
        // A second, separately started observer cross-checks the production timestamp.
        run.Observation = WindowsPlaybackMonitor.FirstOutputAsync(pid, run.Cancellation.Token);
    });
    return run;
}

async Task<SpeechCase> Complete(string name, ClipDraft draft, bool replay = false) => await Finish(Begin(name, draft, replay));

async Task<SpeechCase> Finish(SpeechRun run, bool requireFinalStatus = true)
{
    try { await run.Task.WaitAsync(TimeSpan.FromSeconds(120)); }
    catch (TimeoutException) { speech.Stop(); await run.Task; throw; }
    run.Cancellation.Cancel();
    var observation = run.Observation == null ? null : await run.Observation;
    run.Cancellation.Dispose();
    var audio = run.Draft.AudioPath != null && File.Exists(run.Draft.AudioPath) ? await Measure(run.Draft.AudioPath) : null;
    var times = new Dictionary<string, DateTimeOffset>(run.Draft.Times);
    var result = new SpeechCase(run.Name, run.Draft, run.Replay, run.CalledAt, run.PlayerId, times, observation, audio,
        requireFinalStatus && run.Draft.SpeechStatus == "朗读完成，可重播" && run.Draft.SpeechError == null &&
        times.ContainsKey("audioOutputStarted") && observation?.FirstOutputAt != null && audio?.Rms > 0.00001);
    report.Cases.Add(result);
    WorkflowJson.Save(output, report);
    return result;
}

async Task<AudioMeasurement> Measure(string path)
{
    var info = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(settings.FfplayPath)!, "ffmpeg.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "-v", "error", "-i", path, "-f", "f32le", "-ar", "16000", "-ac", "1", "pipe:1" }) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    using var pcm = new MemoryStream();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.StandardOutput.BaseStream.CopyToAsync(pcm);
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidDataException("Saved speech did not decode: " + (await errorTask).Trim());
    var bytes = pcm.ToArray();
    double sum = 0, peak = 0;
    for (var i = 0; i + 3 < bytes.Length; i += 4)
    {
        var value = BitConverter.ToSingle(bytes, i);
        sum += value * value; peak = Math.Max(peak, Math.Abs(value));
    }
    return new(path, new FileInfo(path).Length, bytes.Length / 4.0 / 16000.0, Math.Sqrt(sum / (bytes.Length / 4)), peak,
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
}

sealed class SpeechRun(string name, ClipDraft draft, bool replay)
{
    public string Name { get; } = name;
    public ClipDraft Draft { get; } = draft;
    public bool Replay { get; } = replay;
    public DateTimeOffset CalledAt { get; } = DateTimeOffset.UtcNow;
    public int? PlayerId;
    public Task Task = Task.CompletedTask;
    public Task<PlaybackObservation>? Observation;
    public CancellationTokenSource Cancellation { get; } = new();
    public TaskCompletionSource OutputReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
sealed record SpeechCase(string Name, ClipDraft Draft, bool Replay, DateTimeOffset CalledAt, int? PlayerId,
    Dictionary<string, DateTimeOffset> Times, PlaybackObservation? Observation, AudioMeasurement? Audio, bool ValidOutput)
{
    [JsonIgnore] public ClipDraft Draft { get; init; } = Draft;
    public string Text { get; } = Draft.Original.SpokenText;
    public string Status { get; } = Draft.SpeechStatus;
    public string? Error { get; } = Draft.SpeechError;
    public object Settings { get; } = new { Draft.Settings.AutoVoice, Draft.Settings.Voice, Draft.Settings.EnglishVoice,
        Draft.Settings.RatePercent, Draft.Settings.VolumePercent, Draft.Settings.PitchHz };
}
sealed record AudioMeasurement(string Path, long Bytes, double DurationSeconds, double Rms, double Peak, string Sha256);
sealed record CheckResult(string Description, bool Passed);
sealed record FailureCleanup(string Status, string? Error, int? PlayerId, bool PlayerExited, bool SpeechStillActive);
sealed class SpeechReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public required string OperatingSystem { get; init; }
    public string Method { get; } = "Production StreamingSpeech; ffplay PID render-session meter; no audio recording; synthetic saved MP3 decoded for metrics.";
    public string Limit { get; } = "Windows nonzero unmuted output is measured; speaker audibility, pronunciation and subjective voice quality need listening.";
    public List<SpeechCase> Cases { get; } = [];
    public List<CheckResult> Checks { get; } = [];
    public double? StopCompletionMilliseconds { get; set; }
    public EndpointState? OutputState { get; set; }
    public FailureCleanup? InjectedFailure { get; set; }
    public bool Passed { get; set; }
    public string? Failure { get; set; }
}

sealed class EndpointState
{
    public bool OriginalMute { get; set; }
    public float OriginalVolume { get; set; }
    public bool? RestoredMute { get; set; }
    public float? FinalVolume { get; set; }
}
sealed class TestEndpoint : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice endpoint;
    public EndpointState State { get; } = new();
    public TestEndpoint()
    {
        endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        State.OriginalMute = endpoint.AudioEndpointVolume.Mute;
        State.OriginalVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        endpoint.AudioEndpointVolume.Mute = false;
    }
    public void Dispose()
    {
        endpoint.AudioEndpointVolume.Mute = State.OriginalMute;
        State.RestoredMute = endpoint.AudioEndpointVolume.Mute;
        State.FinalVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        endpoint.Dispose(); enumerator.Dispose();
    }
}
