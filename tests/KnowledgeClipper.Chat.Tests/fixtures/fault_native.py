"""Isolated process fixture for durability windows; never connects to an external service."""
import json
from pathlib import Path
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stdin.reconfigure(encoding="utf-8")
request=json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
fault=request["input"]
folder=Path(request["cwd"]);folder.mkdir(parents=True,exist_ok=True)
with (folder/"invocations.txt").open("a") as log: log.write(fault+"\n")
sequence=0
def emit(kind,**data):
    global sequence
    sequence+=1
    print(json.dumps({"kind":kind,"durable":True,"seq":sequence,**data},ensure_ascii=False),flush=True)
    while True:
        response=json.loads(sys.stdin.readline())
        if response.get("ack")==sequence:break
def fail():
    emit("error",message="controlled lost receipt: "+fault)
    sys.exit(1)
emit("stage",stage="creating")
if fault=="create-response-lost":fail()
emit("thread",threadId="fixture-thread")
emit("stage",stage="injecting")
if fault=="injection-response-lost":fail()
emit("stage",stage="injected")
emit("stage",stage="sending")
if fault=="turn-response-lost":fail()
emit("turn",turnId="fixture-turn")
emit("delta",itemId="fixture-item",text="部分答案 $x$，不得丢失。")
if fault=="stream-disconnected":fail()
emit("rulesReinjected")
emit("completed",status="completed")
