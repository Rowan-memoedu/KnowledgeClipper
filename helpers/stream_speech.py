"""Audio chunks flow to ffplay immediately; a complete file is retained for task replay."""
import asyncio
import json
import pathlib
import subprocess
import sys
from datetime import datetime, timezone


def emit(name, **details):
    print(json.dumps({"event": name, "at": datetime.now(timezone.utc).isoformat(), **details}), flush=True)


async def main():
    request = json.load(sys.stdin)
    import edge_tts
    audio = pathlib.Path(request["audio"])
    command = [request["ffplay"], "-nodisp", "-autoexit", "-loglevel", "error",
               "-probesize", "1024", "-analyzeduration", "1", "-f", "mp3", "-i", "pipe:0"]
    player = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                              stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    emit("playerStarted", pid=player.pid)  # Process start alone is not proof of physical sound.
    try:
        if request["replay"]:
            with audio.open("rb") as saved:
                while chunk := saved.read(16384):
                    player.stdin.write(chunk)
                    player.stdin.flush()
        else:
            temporary = audio.with_suffix(".partial")
            first = True
            communicate = edge_tts.Communicate(request["text"], request["voice"],
                rate=f'{request["rate"]:+d}%', volume=f'{request["volume"]:+d}%', pitch=f'{request["pitch"]:+d}Hz')
            with temporary.open("wb") as saved:
                async for item in communicate.stream():
                    if item["type"] != "audio":
                        continue
                    chunk = item["data"]
                    if first:
                        emit("firstAudioChunk")
                        first = False
                    saved.write(chunk)
                    player.stdin.write(chunk)
                    player.stdin.flush()
            if first:
                raise RuntimeError("服务未返回音频")
            temporary.replace(audio)
            emit("audioComplete")
        player.stdin.close()
        error = await asyncio.to_thread(player.stderr.read)
        code = await asyncio.to_thread(player.wait)
        if code:
            raise RuntimeError("ffplay 播放失败")
        emit("playbackComplete")
    finally:
        if player.poll() is None:
            player.kill()
            player.wait()


if __name__ == "__main__":
    try:
        if "--prepare" in sys.argv:
            import edge_tts
            emit("helperReady")
        asyncio.run(main())
    except Exception as exc:
        # Avoid logging provider URLs, tokens or the captured text.
        print(type(exc).__name__ + ": 语音或播放连接失败", file=sys.stderr)
        sys.exit(1)
