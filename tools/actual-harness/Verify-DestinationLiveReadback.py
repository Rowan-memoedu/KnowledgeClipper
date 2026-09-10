"""Read-only postflight of ONE already completed Actual Desktop operation.

No business submit, confirmation, service retry, model change or provider write.
The source and expected cards come only from the actual operation's frozen files.
"""
import collections
import hashlib
import json
import pathlib
import sys
import urllib.request

evidence = pathlib.Path(sys.argv[1]).resolve()
data = pathlib.Path(sys.argv[2]).resolve()
output = evidence / "readonly-postflight"
output.mkdir(exist_ok=False)
deck = "零碎知识剪藏::Canary::PhaseB-20260828T081109Z"
root_id = "8zNzfX45XLCSKoPSJ"
root_title = "KnowledgeClipper PhaseB Canary 20260828T081109Z"
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
load = lambda p: json.loads(p.read_text(encoding="utf-8-sig"))


def save(name, value):
    with (output / name).open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)


calls = []
def anki(action, params):
    assert action in {"getActiveProfile", "findNotes", "notesInfo", "cardsInfo"}
    payload = json.dumps({"action": action, "version": 6, "params": params}).encode("utf-8")
    request = urllib.request.Request("http://127.0.0.1:8765", payload, {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=18) as response:
        raw = response.read()
    calls.append(action)
    # Preserve the independent provider response without rewriting content.
    (output / (str(len(calls)) + "-" + action + ".json")).write_bytes(raw)
    result = json.loads(raw)
    assert result["error"] is None, (action, result["error"])
    return result["result"]


status = "FAILED"
failure = None
try:
    completed = load(evidence / "completed.json")
    draft = completed["CardDraft"]["Cards"]
    expected = [(card["Front"], card["Back"]) for card in draft]
    assert completed["Pipeline"] == "Completed"
    assert completed["Playback"] == "NotRequested"
    assert {d["Kind"] for d in completed["Destinations"]} == {"anki", "remnote"}
    assert all(d["State"] == "succeeded" and d["ExternalStateKnown"] for d in completed["Destinations"])
    assert len({d["SourceCardArtifact"]["Token"] for d in completed["Destinations"]}) == 1
    ui = load(evidence / "actual-ui-preview.json")["cards"]
    assert [(card["front"], card["back"]) for card in ui] == expected
    assert anki("getActiveProfile", {}) == "账户 1"
    before = load(evidence / "anki-before.json")["noteIds"]
    after = anki("findNotes", {"query": 'deck:"' + deck + '"'})
    added = sorted(set(after) - set(before))
    assert set(before) <= set(after), "pre-existing Canary notes disappeared"
    assert len(added) == len(expected), "unexpected added-note count"
    notes = anki("notesInfo", {"notes": added})
    actual = [(n["fields"]["Front"]["value"], n["fields"]["Back"]["value"]) for n in notes]
    # Strict actual string equality; no HTML stripping, whitespace repair or oracle.
    assert collections.Counter(actual) == collections.Counter(expected), "Anki exact Front/Back mismatch"
    card_ids = [card for note in notes for card in note["cards"]]
    cards = anki("cardsInfo", {"cards": card_ids})
    assert len(cards) == len(expected) and all(c["deckName"] == deck for c in cards)
    remnote = load(evidence / "remnote-independent-readback.json")["structuredContent"]
    assert remnote["parentRemId"] == root_id and remnote["parentTitle"] == root_title
    rem_cards = remnote["contentStructured"]
    assert len(rem_cards) == len(expected)
    assert collections.Counter(c["headline"] for c in rem_cards) == collections.Counter(f + " >> " + b for f, b in expected)
    assert collections.Counter(c["title"] for c in rem_cards) == collections.Counter(f for f, _ in expected)
    assert all(c["cardDirection"] == "forward" for c in rem_cards)
    jobs = data / "KnowledgeClipper" / "core-v3" / "worker-process-v3" / "jobs"
    requests = []
    for path in sorted(jobs.rglob("request.json")):
        req = load(path)
        if req["operationId"] != completed["OperationId"]:
            continue
        result = load(path.parent / "result.json")
        assert result["status"] == "completed"
        requests.append({"path": str(path), "sha256": sha(path), "workerId": req["workerId"], "operation": req["operation"], "resultPath": str(path.parent / "result.json"), "resultSha256": sha(path.parent / "result.json")})
        if req["workerId"] == "export.remnote":
            assert result["payload"]["documentRemId"] == remnote["remId"]
            assert result["payload"]["outcome"] == "created"
    assert collections.Counter(r["workerId"] for r in requests) == collections.Counter({"content.deepseek": 1, "export.anki": 1, "export.remnote": 1})
    save("request-bindings.json", requests)
    source = (evidence / "capture.payload").read_text(encoding="utf-8-sig")
    cleaned = (evidence / "review.payload").read_text(encoding="utf-8-sig")
    save("source-to-readback.json", {
        "operation": completed["OperationId"], "source": source, "cleaned": cleaned,
        "uiCards": ui, "anki": [{"noteId": n["noteId"], "front": n["fields"]["Front"]["value"], "back": n["fields"]["Back"]["value"], "cardIds": n["cards"]} for n in notes],
        "remnote": {"documentRemId": remnote["remId"], "rootRemId": root_id, "cards": rem_cards},
        "sameBundleToken": completed["CardBundleArtifact"]["Token"], "ankiExact": True, "remnoteExact": True,
        "normalization": "none; exact Unicode Front/Back and RemNote structured headline comparison",
    })
    lines = ["# 实际桌面：原文 → 预览 → 双目标精确回读", "", "本次仅摘录一次、确认一次；后续检查只读，没有重跑 Content 或导出。", "", "## 原文", "", source, "", "## 清洗后", "", cleaned, "", "## 预览与两端结果", ""]
    for front, back in expected:
        lines.extend(["正面：" + front, "", "背面：" + back, ""])
    lines.extend([f"Anki：{len(added)} note / {len(cards)} card，Front/Back 原始字符串逐项完全相同。", "", f"RemNote：{remnote['remId']}，精确位于授权根 {root_id} 下，{len(rem_cards)} 张正向卡片，原文逐项相同。", "", "边界：首次 runner 因 RemNote ExternalWriteCount=null 停止后验；原失败保留。本次只读回读证明实际内容，不把 null 改成 0。重复确认 UI 断言尚未执行，不宣称幂等 UI PASS。"])
    (output / "READABLE.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    status = "PASS-ACTUAL-DESKTOP-SINGLE-CONFIRM-DUAL-EXACT-READBACK"
except Exception as exc:
    failure = type(exc).__name__ + ":" + str(exc)
    raise
finally:
    save("receipt.json", {
        "status": status, "failure": failure, "sourceEvidence": str(evidence),
        "originalFailureReceiptSha256": sha(evidence / "receipt.json"),
        "completedProjectionSha256": sha(evidence / "completed.json"),
        "readOnlyProviderCalls": calls, "newBusinessSubmissions": 0, "newConfirmations": 0, "externalWrites": 0,
        "duplicateUiConfirm": "NOT-RUN; original runner stopped after committed export; no confirmation replay authorized in postflight",
        "files": [{"path": p.name, "bytes": p.stat().st_size, "sha256": sha(p)} for p in sorted(output.iterdir()) if p.is_file()],
    })
    print(status, failure or "", flush=True)
