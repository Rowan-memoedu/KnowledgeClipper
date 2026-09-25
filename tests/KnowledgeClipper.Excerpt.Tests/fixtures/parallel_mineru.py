"""Offline process fixture for MineruCli; never sends a network request."""
import argparse
import json
import os
import time
import zipfile
from pathlib import Path


def main(argv):
    parser = argparse.ArgumentParser()
    parser.add_argument("inputs", nargs="+")
    parser.add_argument("--output-dir")
    args = parser.parse_known_args(argv)[0]
    output = Path(args.output_dir)
    output.mkdir(parents=True, exist_ok=True)
    proxies = [name for name in os.environ if name.lower() in ("http_proxy", "https_proxy", "all_proxy")]
    assert not proxies and os.environ.get("NO_PROXY") == "*"
    assert os.environ.get("MINERU_API_TOKEN") == "offline-parallel-fixture"
    (output / "started.json").write_text(json.dumps({
        "pid": os.getpid(), "started": time.monotonic(), "proxyNames": proxies,
        "direct": True, "input": args.inputs[0]
    }), encoding="utf-8")
    deadline = time.monotonic() + 25
    while not (output.parent / "release").exists():
        if time.monotonic() > deadline:
            raise TimeoutError("Offline fixture release was not received")
        time.sleep(0.02)
    run = output / "mineru-offline"
    run.mkdir()
    text = "Public process fixture " + Path(args.inputs[0]).stem
    (run / "manifest.json").write_text(json.dumps({
        "inputs": args.inputs, "batch_id": "offline-" + str(os.getpid()),
        "request_files": [], "results": [{"state": "done"}]
    }), encoding="utf-8")
    extracted = run / "extracted"
    extracted.mkdir()
    with zipfile.ZipFile(extracted / "mineru-result.zip", "w") as archive:
        archive.writestr("full.md", text)
    with zipfile.ZipFile(extracted / "mineru-result.zip") as archive:
        archive.extractall(extracted)
    (run / "result.md").write_text(text, encoding="utf-8")
    (output / "finished.json").write_text(json.dumps({"finished": time.monotonic()}), encoding="utf-8")
    print("Offline fixture complete")
    return 0
