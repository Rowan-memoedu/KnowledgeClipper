"""Live-test observer: delegate every operation to the configured direct runner.

Only timestamps, stage names and local file names are recorded. URLs, credentials,
headers and remote payloads are never written by this observer.
"""
import importlib.util
import json
import os
import time
from pathlib import Path

provider_path = Path(os.environ["KNOWLEDGECLIPPER_OBSERVED_MINERU"]).resolve()
spec = importlib.util.spec_from_file_location("observed_direct_mineru", provider_path)
provider = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provider)
output = None


def event(stage, **detail):
    if output is None:
        return
    output.mkdir(parents=True, exist_ok=True)
    with (output / "timeline.jsonl").open("a", encoding="utf-8") as stream:
        stream.write(json.dumps({"stage": stage, "unixNanoseconds": time.time_ns(), "pid": os.getpid(), **detail}) + "\n")


original_parse = provider.parse_args


def parse_args(argv):
    global output
    args = original_parse(argv)
    output = Path(args.output_dir)
    return args


provider.parse_args = parse_args


def observe(name, label):
    original = getattr(provider, name)

    def wrapped(*args, **kwargs):
        event(label + "-start")
        succeeded = False
        try:
            result = original(*args, **kwargs)
            succeeded = True
            return result
        finally:
            event(label + "-end", succeeded=succeeded)

    setattr(provider, name, wrapped)


observe("submit_local", "submit")
observe("put_file", "upload")
observe("poll_batch", "parse")
observe("save_results", "download-verify")


def main(argv):
    return provider.main(argv)


def __getattr__(name):
    # Production resume uses the same parse/poll/write/save functions; do not
    # replace a resumed batch with a second submission.
    return getattr(provider, name)
