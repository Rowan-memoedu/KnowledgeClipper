"""Offline provider proving resume never invokes a new submission or a proxy."""
import argparse
import json
import os
import zipfile


def main(args):
    raise AssertionError("Existing completed batch must not submit again")


def parse_args(args):
    parser = argparse.ArgumentParser()
    parser.add_argument("inputs", nargs="+")
    parser.add_argument("--output-dir")
    return parser.parse_known_args(args)[0]


def token_from_env():
    return "offline-fixture"


def poll_batch(batch_id, args, token):
    assert batch_id == "saved-batch"
    assert not any(os.environ.get(key) for key in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"))
    return [{"state": "done", "file_name": "resumed.png"}]


def write_manifest(run_dir, args, batch_id, specs, results):
    (run_dir / "manifest.json").write_text(json.dumps({"inputs": args.inputs, "batch_id": batch_id, "request_files": specs, "results": results}))


def save_results(run_dir, results):
    text = "Resumed OCR without another upload."
    target = run_dir / "resumed"
    target.mkdir()
    with zipfile.ZipFile(target / "mineru-result.zip", "w") as archive:
        archive.writestr("full.md", text)
        archive.writestr("nested/" + "a" * 180 + "_content_list_v2.json", "[]")
    with zipfile.ZipFile(target / "mineru-result.zip") as archive:
        archive.extractall(target)
    (run_dir / "resumed.md").write_text(text)
    return []
