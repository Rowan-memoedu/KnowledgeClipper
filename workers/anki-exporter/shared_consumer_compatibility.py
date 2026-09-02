#!/usr/bin/env python3
"""Pure checks used by the shared Core/Desktop compatibility gate."""

from __future__ import annotations

import re
from pathlib import Path
from typing import Any, Iterable


REQUIRED_OUTPUT_KINDS = {"anki-export-evidence", "anki-connection-status"}
DIRECT_NETWORK_TOKENS = (
    "HttpClient",
    "HttpWebRequest",
    "WebRequest.Create",
    "TcpClient",
    "UdpClient",
    "System.Net.Sockets",
    "AnkiConnectClient",
    "Invoke-RestMethod",
    "Invoke-WebRequest",
)


def require_descriptor(descriptor: dict[str, Any], *, version: str | None = None) -> None:
    if descriptor.get("workerId") != "export.anki":
        raise ValueError("descriptor workerId must be export.anki")
    if version is not None and descriptor.get("workerVersion") != version:
        raise ValueError(f"descriptor workerVersion must be {version}")
    outputs = descriptor.get("outputKinds")
    if not isinstance(outputs, list) or not REQUIRED_OUTPUT_KINDS.issubset(outputs):
        raise ValueError("descriptor must advertise both Anki output kinds")


def require_registry_entry(registry: dict[str, Any]) -> dict[str, Any]:
    workers = registry.get("workers")
    if not isinstance(workers, list):
        raise ValueError("registry workers must be a list")
    matches = [item for item in workers if isinstance(item, dict) and item.get("workerId") == "export.anki"]
    if len(matches) != 1:
        raise ValueError("registry must contain exactly one export.anki entry")
    entry = matches[0]
    if entry.get("entrypoint", {}).get("path") != "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe":
        raise ValueError("registry export.anki entrypoint mismatch")
    if not REQUIRED_OUTPUT_KINDS.issubset(entry.get("outputKinds", [])):
        raise ValueError("registry export.anki outputKinds mismatch")
    return entry


def require_explicit_probe(descriptor: dict[str, Any]) -> dict[str, Any]:
    action = descriptor.get("tests", {}).get("probeConnection")
    expected = {
        "operation": "probe",
        "effectClass": "provider-read-only",
        "externalMutation": False,
        "executionPolicy": "explicit-user-action",
    }
    if not isinstance(action, dict) or any(action.get(key) != value for key, value in expected.items()):
        raise ValueError("probeConnection must be explicit-user-action and provider-read-only")
    return action


def require_sha_bound_config(reference: dict[str, Any], artifact: dict[str, Any], actual_manifest_sha: str) -> None:
    if reference.get("kind") != "anki-export-config" or artifact.get("kind") != "anki-export-config":
        raise ValueError("config reference/artifact kind mismatch")
    expected = reference.get("expectedSha256")
    if not isinstance(expected, str) or not re.fullmatch(r"[0-9A-Fa-f]{64}", expected):
        raise ValueError("config expectedSha256 is missing or malformed")
    if expected.lower() != actual_manifest_sha.lower():
        raise ValueError("config manifest SHA-256 mismatch")
    if reference.get("artifactRef") != artifact.get("artifactId"):
        raise ValueError("config artifact identity mismatch")


def find_direct_network_tokens(sources: Iterable[tuple[Path, str]]) -> list[dict[str, Any]]:
    findings: list[dict[str, Any]] = []
    for path, text in sources:
        if "Anki" not in text and "anki" not in text:
            continue
        for line_number, line in enumerate(text.splitlines(), 1):
            for token in DIRECT_NETWORK_TOKENS:
                if token in line:
                    findings.append({"path": str(path), "line": line_number, "token": token})
    return findings


def first_line(path: Path, pattern: str) -> int:
    compiled = re.compile(pattern)
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if compiled.search(line):
            return line_number
    raise ValueError(f"pattern not found in {path}: {pattern}")

