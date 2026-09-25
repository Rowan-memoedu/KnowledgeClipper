"""Bounded real Edge TTS differential checks using public constructed text only."""
import asyncio
import hashlib
import json
import pathlib
import subprocess
import sys
import time
from datetime import datetime, timezone


async def direct():
    import edge_tts
    request = json.load(sys.stdin)
    path = pathlib.Path(request["audio"])
    count = 0
    try:
        with path.open("wb") as target:
            async with asyncio.timeout(request.get("timeoutSeconds", 25)):
                stream = edge_tts.Communicate(request["text"], request["voice"],
                    rate=f'{request["rate"]:+d}%', volume=f'{request["volume"]:+d}%', pitch=f'{request["pitch"]:+d}Hz')
                async for item in stream.stream():
                    if item["type"] == "audio":
                        target.write(item["data"])
                        count += len(item["data"])
        print(json.dumps({"event": "directComplete", "bytes": count}))
    except Exception as exc:
        # Provider exception strings can include signed URLs; retain only exception type.
        print(json.dumps({"event": "directFailed", "exception": type(exc).__name__, "bytes": count}))
        sys.exit(1)


def main():
    import edge_tts
    root = pathlib.Path(r"D:\Artifacts\KnowledgeClipper\validation\maintenance-speech-live")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    output = root / ("chinese-differential-" + stamp)
    output.mkdir(parents=True)
    helper = pathlib.Path(__file__).resolve().parents[2] / "helpers" / "stream_speech.py"
    ffplay = r"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe"
    cases = [
        ("direct_original", "direct", "苹果在桌上。", "zh-CN-XiaoxiaoNeural"),
        ("helper_original", "helper", "苹果在桌上。", "zh-CN-XiaoxiaoNeural"),
        ("direct_same_voice_english", "direct", "An apple is on the table.", "zh-CN-XiaoxiaoNeural"),
        ("direct_alternate_chinese", "direct", "苹果在桌上。", "zh-CN-XiaoyiNeural"),
    ]
    actual_settings = "--actual-settings" in sys.argv
    if actual_settings:
        cases = [("direct_actual_settings", "direct", "苹果在桌上。", "zh-CN-XiaoxiaoNeural")]
    parameters = [(-3, -18, 0) if actual_settings else (0, 0, 0)] * len(cases)
    parameter_matrix = "--parameter-matrix" in sys.argv
    if parameter_matrix:
        cases = [(name, "direct", "苹果在桌上。", "zh-CN-XiaoxiaoNeural") for name in
                 ("rate_only", "volume_only", "rate_and_volume", "zero_control_last")]
        parameters = [(-3, 0, 0), (0, -18, 0), (-3, -18, 0), (0, 0, 0)]
    report = {"python": sys.executable, "edgeTtsVersion": edge_tts.__version__,
              "helper": str(helper), "startedAt": datetime.now(timezone.utc).isoformat(),
              "voiceEvidence": str(root.parent / "speech-voices-maintenance.json"),
              "scope": "Direct edge_tts.Communicate versus unchanged production helper; no settings changed; no audio metering or listening assertions.",
              "results": []}
    report_path = output / "evidence.json"
    for (name, route, text, voice), (rate, volume, pitch) in zip(cases, parameters):
        print("DIFFERENTIAL starting " + name, flush=True)
        audio = output / (name + ".mp3")
        request = {"text": text, "voice": voice, "audio": str(audio), "replay": False,
                   "rate": rate, "volume": volume, "pitch": pitch, "ffplay": ffplay,
                   "timeoutSeconds": 18 if parameter_matrix else 25}
        command = [sys.executable, "-u", str(helper)] if route == "helper" else [sys.executable, "-u", __file__, "--direct"]
        started = time.monotonic()
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW)
        timed_out = False
        try:
            stdout, stderr = process.communicate(json.dumps(request), timeout=request["timeoutSeconds"] + 5)
        except subprocess.TimeoutExpired:
            timed_out = True
            # Only the exact process we just created and its owned children are terminated.
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=10)
            stdout, stderr = process.communicate(timeout=10)
        events = []
        for line in stdout.splitlines():
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                events.append({"event": "unparsed_stdout"})
        data = audio.read_bytes() if audio.exists() else b""
        error_type = stderr.strip().split(":", 1)[0] if stderr.strip() else None
        result = {"name": name, "route": route, "text": text, "voice": voice,
                  "rate": request["rate"], "volume": request["volume"], "pitch": request["pitch"],
                  "requestTimeoutSeconds": request["timeoutSeconds"],
                  "elapsedSeconds": round(time.monotonic()-started, 3), "timedOut": timed_out,
                  "exitCode": process.returncode, "events": events, "errorType": error_type,
                  "audio": str(audio), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest() if data else None}
        report["results"].append(result)
        report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        print(f"DIFFERENTIAL {name}: exit={process.returncode}, timedOut={timed_out}, bytes={len(data)}, error={error_type}", flush=True)
    print(str(report_path), flush=True)


if __name__ == "__main__":
    if "--direct" in sys.argv:
        asyncio.run(direct())
    else:
        main()
