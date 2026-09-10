"""Transport-only fixture: output is text, not synthesized audio."""
import json
import pathlib
import sys

if "--prepare" in sys.argv:
    print(json.dumps({"event": "helperReady"}), flush=True)
request = json.load(sys.stdin)
audio = pathlib.Path(request["audio"])
if not request["replay"]:
    audio.write_text(request["text"], encoding="utf-8")
    print(json.dumps({"event": "audioComplete"}), flush=True)
else:
    assert audio.exists()
print(json.dumps({"event": "playbackComplete"}), flush=True)
