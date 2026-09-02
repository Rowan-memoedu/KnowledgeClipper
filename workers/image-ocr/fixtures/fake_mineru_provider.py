"""Network-free MinerU-shaped fixture used only by Image/OCR offline probes."""

from __future__ import annotations

import zipfile
from pathlib import Path


_items = []


def submit_local(paths, args, token):
    global _items
    _items = [{"data_id": f"fake-{index:04d}", "file_name": Path(path).name} for index, path in enumerate(paths, 1)]
    return "offline-fake-batch", _items


def poll_batch(batch_id, args, token):
    return [{"data_id": item["data_id"], "state": "done", "full_zip_url": "fake://" + item["data_id"]} for item in _items]


def download_file(url, path):
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("full.md", "# OFFLINE FAKE MINERU FIXTURE\n")
