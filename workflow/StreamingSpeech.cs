using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Workflow;

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

    public async Task SpeakAsync(ClipDraft draft, bool replay, Action changed, Action<int>? playerStarted = null)
    {
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
            Directory.CreateDirectory(audioRoot);
            var audio = Path.Combine(audioRoot, draft.Id + "-" + Guid.NewGuid().ToString("N") + ".mp3");
            var reuse = replay && draft.AudioPath != null && File.Exists(draft.AudioPath);
            if (!reuse) incompleteAudio = Path.ChangeExtension(audio, ".partial");
            PreparedHelper selectedHelper;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (prepared != null && prepared.PythonPath == settings.PythonPath && !prepared.Process.HasExited)
                { selectedHelper = prepared; prepared = null; }
                else
                {
                    ClosePrepared(prepared); prepared = null;
                    selectedHelper = StartHelper(settings, warm: false);
                }
            }
            process = selectedHelper.Process;
            Prepare(settings);
            using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            var stderr = process.StandardError.ReadToEndAsync();
            var voice = settings.AutoVoice && !draft.Original.SpokenText.Any(c => c is >= '\u3400' and <= '\u9fff') ? settings.EnglishVoice : settings.Voice;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { text = draft.Original.SpokenText, voice,
                rate = settings.RatePercent, volume = settings.VolumePercent, pitch = settings.PitchHz, ffplay = settings.FfplayPath,
                audio = reuse ? draft.AudioPath : audio, replay = reuse }, WorkflowJson.Options).AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();
            Update(() => {
                draft.SpeechStatus = reuse ? "重播中" : "正在连接朗读"; draft.SpeechError = null;
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
                    if (name == "firstAudioChunk") draft.SpeechStatus = "已收到音频，正在开始播放";
                    if (name == "audioComplete") draft.AudioPath = audio;
                });
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("朗读组件未能完成播放：" + (await stderr.ConfigureAwait(false)).Trim());
            Update(() => draft.SpeechStatus = "朗读完成，可重播");
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
}
