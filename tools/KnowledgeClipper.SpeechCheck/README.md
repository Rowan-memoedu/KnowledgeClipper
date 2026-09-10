# Speech check

Runs the production `StreamingSpeech` interface with synthetic English, Chinese and mixed text. Each case independently observes the exact ffplay process's Windows audio-session peak, then decodes the task's saved MP3 to compare rate and volume. It checks replay without synthesis, stop, replacement and same-draft restart.

```powershell
$env:NUGET_PACKAGES = 'D:\Caches\NuGet\packages'
& 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe' run --project 'tools\KnowledgeClipper.SpeechCheck\KnowledgeClipper.SpeechCheck.csproj' -c Release -- 'D:\Projects\RemNote Quick Clipper' 'D:\Artifacts\KnowledgeClipper\validation\speech.json'
```

Append `--smoke` for one English case (`--prepared` prepares the empty helper before the first call), `--state-only` for stop/replacement checks, or `--latency` for cold/prepared timing, replay and prepared-process lifetime checks. Append `--temporarily-unmute` only when audible validation is intended; it preserves volume and restores the original default endpoint mute state in `finally`. It does not modify other application sessions or restore volume over concurrent user changes.

The CLI needs the configured Python/edge-tts and ffplay/ffmpeg, internet access and a usable Windows output device. The production content CLI does not initialize an audio device. Only synthetic task audio is saved; no microphone or loopback recording is taken. Windows nonzero unmuted output is not a claim about physical speaker audibility or pronunciation quality. The independent-work cancellation check is a state test; actual content quality and integration have separate checks.
