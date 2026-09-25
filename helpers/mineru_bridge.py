"""Reuse the installed direct MinerU runner and resume completed parsing batches."""
from __future__ import annotations

import importlib.util
import json
import os
import sys
import uuid
from pathlib import Path

sys.dont_write_bytecode = True
from mineru_direct import install_physical_direct


def output_path(value: str) -> str:
    path = str(Path(value).resolve())
    if os.name != "nt" or path.startswith("\\\\?\\"):
        return path
    return "\\\\?\\UNC\\" + path[2:] if path.startswith("\\\\") else "\\\\?\\" + path


def run(argv: list[str]) -> int:
    provider_path = Path(argv[0]).resolve()
    remaining = argv[1:]
    resume = None
    if "--resume-manifest" in remaining:
        index = remaining.index("--resume-manifest")
        resume = Path(remaining[index + 1]).resolve()
        remaining = remaining[:index] + remaining[index + 2:]
    if "--output-dir" in remaining:
        index = remaining.index("--output-dir")
        remaining[index + 1] = output_path(remaining[index + 1])
    spec = importlib.util.spec_from_file_location("installed_mineru", provider_path)
    if spec is None or spec.loader is None:
        raise RuntimeError("MinerU provider could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    install_physical_direct(module)
    if resume is None:
        return module.main(remaining)
    args = module.parse_args(remaining)
    manifest = json.loads(resume.read_text(encoding="utf-8"))
    if len(args.inputs) != 1 or [str(Path(p).resolve()) for p in manifest["inputs"]] != [str(Path(args.inputs[0]).resolve())]:
        raise ValueError("Resume source does not match this image")
    token = module.token_from_env()
    results = module.poll_batch(manifest["batch_id"], args, token)
    run_dir = Path(args.output_dir) / ("mineru-resume-" + uuid.uuid4().hex[:12])
    run_dir.mkdir(parents=True, exist_ok=False)
    module.write_manifest(run_dir, args, manifest["batch_id"], manifest["request_files"], results)
    failures = module.save_results(run_dir, results)
    if failures:
        raise module.MineruError("; ".join(failures))
    print("Done. Resumed existing MinerU batch.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(run(sys.argv[1:]))
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
