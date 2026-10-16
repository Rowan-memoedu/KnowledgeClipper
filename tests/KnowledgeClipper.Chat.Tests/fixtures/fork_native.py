"""Controlled fork receipt windows; no external calls."""
import json
from pathlib import Path
import sys
sys.stdout.reconfigure(encoding="utf-8")
request=json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
root=Path(request["cwd"]);root.mkdir(parents=True,exist_ok=True)
with (root/"invocations.jsonl").open("a") as log:log.write(json.dumps(request)+"\n")
sequence=0
def emit(kind,**data):
    global sequence
    sequence+=1
    print(json.dumps({"kind":kind,"durable":True,"seq":sequence,**data}),flush=True)
    while json.loads(sys.stdin.readline()).get("ack")!=sequence:pass
if request["action"]=="verifyFork":
    assert request["threadId"]=="fork-thread"
    emit("forkVerified")
else:
    emit("stage",stage="forking")
    if request["model"]!="lost-fork":emit("forked",threadId="fork-thread")
    emit("error",message="controlled response loss")
    sys.exit(1)
