"""Thin official App Server adapter. Stdout is a bounded business event stream.

An acknowledgement from the .NET owner follows durable identity/stage writes;
no native request is replayed by this process. Authentication stays with Codex.
"""
from __future__ import annotations

import base64
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading

from openai_codex.client import CodexClient, CodexConfig
from pydantic import BaseModel, ConfigDict


class Response(BaseModel):
    model_config = ConfigDict(extra="allow")


def body(value):
    return value.model_dump(mode="json", by_alias=True) if hasattr(value, "model_dump") else value


def resolve_runtime(config):
    selected = config.get("codexBin") or os.environ.get("KNOWLEDGECLIPPER_CODEX_BIN")
    expected = config.get("runtimeVersion") or os.environ.get("KNOWLEDGECLIPPER_CODEX_RUNTIME_VERSION", "0.160.1")
    owned = Path("D:/Applications/RemNoteQuickClipperRuntime/codex-runtime/node_modules/@openai")
    candidates = [Path(selected)] if selected else list(owned.glob("codex-win32-*/vendor/*/bin/codex.exe"))
    if not candidates:
        candidates = list((Path.home() / "AppData/Local/OpenAI/Codex/bin").glob("*/codex.exe"))
    matches = []
    for path in candidates:
        if not path.is_file():
            continue
        result = subprocess.run([str(path), "--version"], capture_output=True, text=True,
                                timeout=15, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if result.returncode == 0 and result.stdout.strip() == "codex-cli " + expected:
            matches.append(path)
    if len(matches) != 1:
        raise RuntimeError("未找到唯一的已验收 Codex runtime " + expected +
                           "；可用 KNOWLEDGECLIPPER_CODEX_BIN 指定，升级后需复测。")
    return str(matches[0])


class Channel:
    def __init__(self):
        self.acks = queue.Queue()
        self.cancel = threading.Event()
        self.dead = threading.Event()
        self.output_lock = threading.Lock()
        self.sequence = 0
        self.client = None
        self.thread_id = self.turn_id = None
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        for line in sys.stdin:
            command = json.loads(line)
            if command.get("command") == "cancel":
                self.cancel.set()
                self.interrupt()
            elif "ack" in command:
                self.acks.put(command["ack"])
        self.dead.set()
        self.cancel.set()
        self.interrupt()

    def interrupt(self):
        if self.client and self.thread_id and self.turn_id:
            try:
                self.client.turn_interrupt(self.thread_id, self.turn_id)
                self.emit("interruptAcknowledged")
            except Exception:
                self.emit("interruptUnknown")

    def emit(self, kind, durable=False, **data):
        with self.output_lock:
            self.sequence += 1
            seq = self.sequence
            print(json.dumps({"kind": kind, "seq": seq, "durable": durable, **data},
                             ensure_ascii=False), flush=True)
        if durable:
            while True:
                try:
                    if self.acks.get(timeout=1) != seq:
                        raise RuntimeError("持久化确认序号不符；未继续发送。")
                    break
                except queue.Empty:
                    if self.dead.is_set():
                        raise RuntimeError("本机草稿写入未确认；未继续发送。")


def visible_item(item, media_root):
    kind = item.get("type")
    if kind == "agentMessage":
        return {"id": item["id"], "parts": [{"kind": "text", "text": item.get("text", "")}],
                "phase": item.get("phase")}
    if kind != "imageGeneration":
        return None
    encoded = item.get("result")
    saved = item.get("savedPath")
    raw = base64.b64decode(encoded, validate=True) if encoded else Path(saved).read_bytes() if saved else None
    if raw is None or not raw.startswith(b"\x89PNG\r\n\x1a\n") or not 24 <= len(raw) <= 33554432:
        raise RuntimeError("原生图片尚无可验证的 PNG；保留原回合身份。")
    if encoded and saved and Path(saved).is_file() and Path(saved).read_bytes() != raw:
        raise RuntimeError("原生图片事件与文件字节不符。")
    digest = hashlib.sha256(raw).hexdigest()
    media_root.mkdir(parents=True, exist_ok=True)
    target = media_root / (digest + ".png")
    if target.exists() and target.read_bytes() != raw:
        raise RuntimeError("本地图片指纹冲突。")
    if not target.exists():
        temp = target.with_suffix(".tmp")
        temp.write_bytes(raw)
        temp.replace(target)
    return {"id": item["id"], "parts": [{"kind": "image", "image": {
        "path": str(target), "sha256": digest, "role": "answer", "index": 1}}]}


def run(request, channel):
    os.environ.pop("CODEX_APP_TOOLS_PIPE_PATH", None)
    cwd = Path(request["cwd"])
    cwd.mkdir(parents=True, exist_ok=True)
    options = ("memories.use_memories=false", "memories.generate_memories=false", "project_doc_max_bytes=0",
               'web_search="disabled"', "features.shell_tool=false", "features.multi_agent=false")
    with CodexClient(CodexConfig(codex_bin=resolve_runtime(request), cwd=str(cwd),
                                experimental_api=True, client_name="knowledge_clipper",
                                client_title="KnowledgeClipper", config_overrides=options)) as client:
        client.initialize()
        channel.client = client
        action = request["action"]
        if action in ("fork", "verifyFork"):
            source_id, boundary = request["sourceThreadId"], request["lastTurnId"]
            source = body(client.thread_read(source_id, include_turns=True))["thread"]
            turns = source.get("turns", [])
            matches = [i for i, turn in enumerate(turns) if turn["id"] == boundary]
            if len(matches) != 1 or turns[matches[0]]["status"] not in ("completed", "interrupted"):
                raise RuntimeError("分叉位置不是已完成的原生回合；尚未创建分叉。")
            expected = [turn["id"] for turn in turns[:matches[0] + 1]]
            if action == "fork":
                channel.emit("stage", durable=True, stage="forking")
                result = client.thread_fork(source_id, {"lastTurnId": boundary, "excludeTurns": True})
                channel.thread_id = result.thread.id
                channel.emit("forked", durable=True, threadId=channel.thread_id)
            else:
                channel.thread_id = request.get("threadId")
            if not channel.thread_id or channel.thread_id == source_id:
                raise RuntimeError("缺少精确的新分叉身份；不会重新分叉。")
            copied = body(client.thread_read(channel.thread_id, include_turns=True))["thread"]
            if [turn["id"] for turn in copied.get("turns", [])] != expected:
                raise RuntimeError("原生分叉历史与指定回合边界不符；保留新身份，禁止发送。")
            channel.emit("forkVerified", durable=True, threadId=channel.thread_id, lastTurnId=boundary)
            return
        if action == "models":
            account = body(client.account_read())
            if (account.get("account") or {}).get("type") != "chatgpt":
                raise RuntimeError("Codex 未以 ChatGPT 登录；请使用官方 Codex 登录入口完成登录。")
            models = body(client.model_list()).get("data", [])
            channel.emit("models", models=[{"id": m["model"], "name": m.get("displayName", m["model"]),
                "efforts": [e["reasoningEffort"] for e in m.get("supportedReasoningEfforts", [])],
                "inputModalities": m.get("inputModalities", ["text"]),
                "defaultEffort": m.get("defaultReasoningEffort")} for m in models if not m.get("hidden")])
            return
        channel.thread_id = request.get("threadId")
        channel.turn_id = request.get("turnId") if action == "recover" else None
        media_root = Path(request["mediaRoot"])
        if action == "recover":
            if not channel.thread_id or not channel.turn_id:
                raise RuntimeError("结果未知且缺少精确 Thread/Turn ID；不会新建或重发。")
            thread = body(client.thread_read(channel.thread_id, include_turns=True))["thread"]
            turns = [t for t in thread.get("turns", []) if t["id"] == channel.turn_id]
            if len(turns) != 1:
                raise RuntimeError("未读到原回合；不会重发。")
            turn = turns[0]
            for item in turn.get("items", []):
                visible = visible_item(item, media_root)
                if visible:
                    channel.emit("item", item=visible)
            if turn["status"] == "inProgress":
                raise RuntimeError("原回合仍在运行；保留输入和身份，稍后核对。")
            channel.emit("completed", durable=True, status=turn["status"], error=turn.get("error"))
            return
        if action != "send":
            raise RuntimeError("未知业务动作。")
        if not channel.thread_id:
            channel.emit("stage", durable=True, stage="creating")
            created = client.thread_start({"cwd": str(cwd), "ephemeral": False, "model": request["model"],
                "approvalPolicy": "never", "sandbox": "read-only", "serviceName": "knowledge_clipper",
                "baseInstructions": "这是学习问答。应用注入的最新规则指定当前回答方式。不要执行命令或读其他文件；仅处理本轮材料。"})
            channel.thread_id = created.thread.id
            channel.emit("thread", durable=True, threadId=channel.thread_id)
        else:
            client.thread_resume(channel.thread_id, {"excludeTurns": True})
        if channel.cancel.is_set():
            channel.emit("cancelledBeforeSend", durable=True)
            return
        channel.emit("stage", durable=True, stage="injecting")
        client.request("thread/inject_items", {"threadId": channel.thread_id, "items": [{"type": "message",
            "role": "developer", "content": [{"type": "input_text", "text": request["instructions"]}]}]}, response_model=Response)
        channel.emit("stage", durable=True, stage="injected")
        inputs = []
        if request.get("input"):
            inputs.append({"type": "text", "text": request["input"]})
        for asset in request.get("images", []):
            image = Path(asset["path"])
            if hashlib.sha256(image.read_bytes()).hexdigest() != asset["sha256"]:
                raise RuntimeError("输入图片已改变；未发送问题。")
            inputs.append({"type": "localImage", "path": str(image)})
        if not inputs:
            raise RuntimeError("请输入文字或图片。")
        if channel.cancel.is_set():
            channel.emit("cancelledBeforeSend", durable=True)
            return
        channel.emit("stage", durable=True, stage="sending")
        params = {"model": request["model"]}
        if request.get("effort"):
            params["effort"] = request["effort"]
        turn = client.turn_start(channel.thread_id, inputs, params)
        channel.turn_id = turn.turn.id
        channel.emit("turn", durable=True, turnId=channel.turn_id)
        if channel.cancel.is_set():
            channel.interrupt()
        compacted_items = set()
        try:
            while True:
                event = client.next_turn_notification(channel.turn_id)
                data = body(event.payload)
                if event.method == "item/agentMessage/delta":
                    channel.emit("delta", itemId=data["itemId"], text=data.get("delta", ""))
                elif event.method in ("item/started", "item/completed"):
                    item = data.get("item", {})
                    if item.get("type") == "contextCompaction" and event.method == "item/completed":
                        # Mid-turn injection is pending input in 0.160.1 and can trigger another
                        # response/compaction cycle. Coalesce confirmed events and inject at idle.
                        if item["id"] not in compacted_items:
                            compacted_items.add(item["id"])
                            channel.emit("compactionCompleted", durable=True, itemId=item["id"])
                    elif event.method == "item/completed" or item.get("type") == "agentMessage":
                        visible = visible_item(item, media_root)
                        if visible:
                            channel.emit("item", item=visible)
                elif event.method == "turn/completed":
                    if compacted_items:
                        client.request("thread/inject_items", {"threadId": channel.thread_id, "items": [{"type": "message",
                            "role": "developer", "content": [{"type": "input_text", "text": request["instructions"]}]}]}, response_model=Response)
                        channel.emit("rulesReinjected", durable=True, itemIds=sorted(compacted_items), timing="afterTurnCompleted")
                    channel.emit("completed", durable=True, status=data["turn"]["status"], error=data["turn"].get("error"))
                    break
        finally:
            client.unregister_turn_notifications(channel.turn_id)


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    channel = Channel()
    try:
        run(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig")), channel)
    except Exception as error:
        channel.emit("error", message=str(error))
        sys.exit(1)
