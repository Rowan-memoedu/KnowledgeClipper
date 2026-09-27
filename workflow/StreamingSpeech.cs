using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public enum SpeechPurpose { Selection, Translation, Sentence }

public sealed class StreamingSpeech(string helperPath, string audioRoot) : IDisposable
{
    private CancellationTokenSource? current;
    private readonly object gate = new();
    private readonly Dictionary<string, CancellationTokenSource> attempts = [];
    private PreparedHelper? prepared;
    private bool disposed;
    public bool IsPlaying { get { lock (gate) return current != null; } }
    public void Stop() { lock (gate) { current?.Cancel(); } }
    public Task PreparationReady { get { lock (gate) return prepared?.Ready ?? Task.CompletedTask; } }
    public int? PreparedProcessId { get { lock (gate) return prepared?.Process.Id; } }

    // One empty helper imports dependencies and waits on stdin. It receives no captured material.
    public void Prepare(LanguageSettings settings)
    {
        lock (gate)
        {
            if (disposed) return;
            if (prepared != null && prepared.PythonPath == settings.PythonPath && !prepared.Process.HasExited) return;
            ClosePrepared(prepared); prepared = null;
            try { prepared = StartHelper(settings, warm: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true; current?.Cancel();
            ClosePrepared(prepared); prepared = null;
        }
    }

    public Task SpeakAsync(ClipDraft draft, bool replay, Action changed, Action<int>? playerStarted = null) =>
        SpeakTextAsync(draft, draft.Original.SelectedText, SpeechPurpose.Selection, replay, changed, playerStarted);

    // The caller resolves word/context eligibility. Playback preserves the source
    // recording's exact text, voice and synthesis settings without changing the new draft.
    // Invalid/missing recordings fail explicitly; this API never falls back to TTS.
    public Task SpeakRecordedAsync(ClipDraft draft, string sourceText, LanguageSettings sourceSettings, string audioPath,
        Action changed, Action<int>? playerStarted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentNullException.ThrowIfNull(sourceSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        var root = Path.GetFullPath(audioRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(audioPath);
        if (!Path.IsPathFullyQualified(audioPath) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).EndsWith("-" + AudioCacheKey(sourceText, SpeechPurpose.Selection, sourceSettings) + ".mp3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("历史录音的路径、文字或音色参数与原记录不符。");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) throw new FileNotFoundException("历史录音不存在或尚未保存完整。", path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("历史录音必须是音频缓存中的实际文件。");
        for (var directory = file.Directory; directory != null &&
            !string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase);
            directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("历史录音不能通过链接目录离开音频缓存。");
        return SpeakCoreAsync(draft, sourceText, SpeechPurpose.Selection, true, changed, playerStarted, new(path, sourceSettings));
    }

    public static string SelectVoice(string text, LanguageSettings settings)
    {
        if (!settings.AutoVoice || !text.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) return settings.Voice;
        // English learning must not let a multilingual voice reinterpret a short word
        // as another language. Keep the same speaker's English-only counterpart.
        return settings.EnglishVoice switch
        {
            "en-US-AvaMultilingualNeural" => "en-US-AvaNeural",
            "en-US-AndrewMultilingualNeural" => "en-US-AndrewNeural",
            "en-US-BrianMultilingualNeural" => "en-US-BrianNeural",
            "en-US-EmmaMultilingualNeural" => "en-US-EmmaNeural",
            _ => settings.EnglishVoice
        };
    }

    public static string SpokenText(string text, LanguageSettings settings)
    {
        if (!settings.AutoVoice || !text.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) return text;
        // Keep the captured material intact. Mixed automatic English playback skips Han text
        // before synthesis, including when the user's selected English voice is multilingual.
        var spoken = new StringBuilder(text.Length);
        var filtered = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var han = rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
                >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FA1F or >= 0x30000 and <= 0x3347F;
            spoken.Append(han ? " " : rune.ToString());
            filtered |= han;
        }
        return filtered ? Regex.Replace(spoken.ToString(), @"\s+", " ").Trim() : text;
    }

    // Persisted audio is reusable only for the same purpose, exact text, voice and synthesis settings.
    public static string AudioCacheKey(string text, SpeechPurpose purpose, LanguageSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { provider = "mimo-v2.5-tts", text, purpose, voice = SelectVoice(text, settings), spokenText = SpokenText(text, settings),
            settings.RatePercent, settings.VolumePercent, settings.PitchHz })))).ToLowerInvariant();

    public Task SpeakTextAsync(ClipDraft draft, string text, SpeechPurpose purpose, bool replay, Action changed, Action<int>? playerStarted = null) =>
        SpeakCoreAsync(draft, text, purpose, replay, changed, playerStarted, null);

    private async Task SpeakCoreAsync(ClipDraft draft, string text, SpeechPurpose purpose, bool replay, Action changed,
        Action<int>? playerStarted, RecordedAudio? recording)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var invokedAt = DateTimeOffset.UtcNow;
        CancellationTokenSource mine;
        lock (gate) { current?.Cancel(); current = mine = new(); attempts[draft.Id] = mine; }
        var token = mine.Token;
        void Update(Action action)
        {
            lock (gate)
            {
                if (!attempts.TryGetValue(draft.Id, out var active) || !ReferenceEquals(active, mine)) return;
                action(); changed();
            }
        }
        using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? observation = null;
        string? incompleteAudio = null;
        Process? process = null;
        Process? playerProcess = null;
        try
        {
            var settings = draft.Settings;
            var synthesisSettings = recording?.Settings ?? settings;
            Directory.CreateDirectory(audioRoot);
            var cacheKey = AudioCacheKey(text, purpose, synthesisSettings);
            var cached = recording?.Path ?? Path.Combine(audioRoot, draft.Id + "-" + cacheKey + ".mp3");
            // Unique partial paths prevent a cancelled request from deleting the next request's stream.
            var audio = Path.Combine(audioRoot, draft.Id + "-" + Guid.NewGuid().ToString("N") + ".mp3");
            var reuse = recording != null || replay && File.Exists(cached);
            if (!reuse) incompleteAudio = Path.ChangeExtension(audio, ".partial");
            PreparedHelper selectedHelper;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (prepared != null && prepared.PythonPath == settings.PythonPath && !prepared.Process.HasExited)
                {
                    // A saved recording does not need edge_tts. Do not queue it
                    // behind dependency imports in an unfinished warm helper.
                    if (reuse && prepared.Ready?.IsCompleted == false) selectedHelper = StartHelper(settings, warm: false);
                    else { selectedHelper = prepared; prepared = null; }
                }
                else
                {
                    ClosePrepared(prepared); prepared = null;
                    selectedHelper = StartHelper(settings, warm: false);
                }
            }
            process = selectedHelper.Process;
            using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            var stderr = process.StandardError.ReadToEndAsync();
            var voice = SelectVoice(text, synthesisSettings);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { text = SpokenText(text, synthesisSettings), voice,
                rate = synthesisSettings.RatePercent, volume = synthesisSettings.VolumePercent, pitch = synthesisSettings.PitchHz, ffplay = settings.FfplayPath,
                audio = reuse ? cached : audio, replay = reuse, play = replay || settings.SpeechAutoPlay || purpose == SpeechPurpose.Sentence }, WorkflowJson.Options).AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();
            // Send the current request before starting the next idle helper.
            Prepare(settings);
            Update(() => {
                draft.SpeechStatus = reuse ? "重播中" : "正在连接朗读"; draft.SpeechError = null;
                if (reuse && purpose != SpeechPurpose.Sentence) draft.AudioPath = cached;
                foreach (var name in new[] { "helperReady", "playerStarted", "firstAudioChunk", "audioOutputStarted", "audioComplete", "playbackComplete" }) draft.Times.TryRemove(name, out _);
                draft.Times["speechStarted"] = invokedAt;
            });
            if (selectedHelper.Ready != null) await selectedHelper.Ready.WaitAsync(token).ConfigureAwait(false);
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is string line)
            {
                using var data = JsonDocument.Parse(line);
                var name = data.RootElement.GetProperty("event").GetString()!;
                if (name == "playerStarted" && data.RootElement.TryGetProperty("pid", out var pid))
                {
                    var processId = pid.GetInt32();
                    try { playerProcess = Process.GetProcessById(processId); _ = playerProcess.Handle; }
                    catch (ArgumentException) { playerProcess = null; }
                    playerStarted?.Invoke(processId);
                    observation = ObserveOutputAsync(processId);
                }
                Update(() => {
                    draft.Times[name] = data.RootElement.TryGetProperty("at", out var at) ? at.GetDateTimeOffset() : DateTimeOffset.UtcNow;
                    if (name == "firstAudioChunk") draft.SpeechStatus = settings.SpeechAutoPlay || replay || purpose == SpeechPurpose.Sentence ? "已收到音频，正在开始播放" : "已收到音频，正在保存";
                    if (name == "audioComplete")
                    {
                        File.Move(audio, cached, true);
                        if (purpose != SpeechPurpose.Sentence) draft.AudioPath = cached;
                    }
                });
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("朗读组件未能完成播放：" + (await stderr.ConfigureAwait(false)).Trim());
            Update(() => draft.SpeechStatus = settings.SpeechAutoPlay || replay || purpose == SpeechPurpose.Sentence ? "朗读完成，可重播" : "音频已保存，点击播放可听取");
        }
        catch (OperationCanceledException) { Update(() => draft.SpeechStatus = "朗读已停止"); }
        catch (Exception) when (token.IsCancellationRequested) { Update(() => draft.SpeechStatus = "朗读已停止"); }
        catch (Exception ex) { Update(() => { draft.SpeechStatus = "朗读失败，可重新朗读"; draft.SpeechError = ex.Message; }); }
        finally
        {
            observationCancellation.Cancel();
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                process.Dispose();
            }
            if (playerProcess != null)
            {
                try
                {
                    if (!playerProcess.HasExited) playerProcess.Kill();
                    await playerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                playerProcess.Dispose();
            }
            if (observation != null) await observation.ConfigureAwait(false);
            if (incompleteAudio != null)
            {
                try { File.Delete(incompleteAudio); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            lock (gate) {
                if (ReferenceEquals(current, mine)) current = null;
                if (attempts.TryGetValue(draft.Id, out var active) && ReferenceEquals(active, mine)) attempts.Remove(draft.Id);
            }
            mine.Dispose();
        }

        async Task ObserveOutputAsync(int processId)
        {
            PlaybackObservation result;
            try { result = await WindowsPlaybackMonitor.FirstOutputAsync(processId, observationCancellation.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
            { return; } // Optional metering cannot interrupt speech or the independent content branch.
            if (result.FirstOutputAt is DateTimeOffset started)
                Update(() => {
                    if (token.IsCancellationRequested) return;
                    draft.Times["audioOutputStarted"] = started;
                    if (draft.SpeechError == null && !draft.Times.ContainsKey("playbackComplete")) draft.SpeechStatus = "正在朗读";
                });
        }
    }

    private PreparedHelper StartHelper(LanguageSettings settings, bool warm)
    {
        var info = new ProcessStartInfo { FileName = settings.PythonPath, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("-u"); info.ArgumentList.Add(helperPath);
        if (warm) info.ArgumentList.Add("--prepare");
        var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动朗读组件。");
        return new(process, settings.PythonPath, warm ? ReadReadyAsync(process) : null);
    }

    private static async Task<string?> ReadReadyAsync(Process process)
    {
        try { return await process.StandardOutput.ReadLineAsync().ConfigureAwait(false); }
        catch (IOException) { return null; }
    }

    private static void ClosePrepared(PreparedHelper? helper)
    {
        if (helper == null) return;
        try { if (!helper.Process.HasExited) helper.Process.Kill(); helper.Process.WaitForExit(3000); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        helper.Process.Dispose();
    }

    private sealed record PreparedHelper(Process Process, string PythonPath, Task<string?>? Ready);
    private sealed record RecordedAudio(string Path, LanguageSettings Settings);
}
