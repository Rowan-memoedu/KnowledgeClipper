from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


TREE_ALGORITHM = "sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def inventory(root: Path) -> dict:
    rows = []
    files = []
    seen = set()
    for path in root.rglob("*"):
        if not path.is_file() or path.is_symlink():
            continue
        relative = path.relative_to(root).as_posix()
        folded = relative.casefold()
        if folded in seen:
            raise RuntimeError(f"runtime path collision: {relative}")
        seen.add(folded)
        size = path.stat().st_size
        digest = sha256(path)
        files.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
    rows.sort()
    files.sort(key=lambda item: item["path"])
    return {
        "fileCount": len(files),
        "totalBytes": sum(item["bytes"] for item in files),
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
        "files": files,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-root", required=True, type=Path)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--component-version", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = args.publish_root.resolve()
    checkpoint = args.checkpoint.resolve()
    output = args.output.resolve()
    if output.exists():
        raise RuntimeError(f"append-only output exists: {output}")
    if output == root or root in output.parents:
        raise RuntimeError("runtime manifest must be outside the publish payload root")
    measured = inventory(root)
    required = {
        "KnowledgeClipper.Orchestrator.exe",
        "KnowledgeClipper.Orchestrator.dll",
        "KnowledgeClipper.Orchestrator.deps.json",
        "KnowledgeClipper.Orchestrator.runtimeconfig.json",
    }
    present = {item["path"] for item in measured["files"]}
    if not required <= present:
        raise RuntimeError(f"runtime payload missing: {sorted(required - present)}")
    project = Path(__file__).resolve().parents[1]
    document = {
        "schemaVersion": "knowledge-clipper-orchestrator-runtime-manifest.v1",
        "checkpoint": {"path": checkpoint.relative_to(project).as_posix(), "sha256": sha256(checkpoint)},
        "component": {"id": "knowledge-clipper.orchestrator", "version": args.component_version},
        "selfContained": True,
        "runtimeIdentifier": "win-x64",
        "entrypoint": "KnowledgeClipper.Orchestrator.exe",
        "treeAlgorithm": TREE_ALGORITHM,
        **measured,
    }
    output.parent.mkdir(parents=True)
    output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"manifest": str(output), "sha256": sha256(output), **{k: document[k] for k in ("fileCount", "totalBytes", "treeSha256")}}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
