"""Source-bound offline qualification; never builds/promotes a product authority or calls providers."""
from __future__ import annotations
import argparse
import copy
import hashlib
import json
from pathlib import Path
from urllib.parse import urldefrag
from jsonschema import Draft202012Validator, FormatChecker
import validate_contracts as contracts


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def evidence(path):
    path = Path(path).resolve(strict=True)
    if ".tmp" in str(path):
        raise ValueError("Temporary evidence path")
    data = path.read_bytes()
    return {"path": str(path), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest().upper()}


def write_new(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("test-root", "adapter-root", "build-root", "sdk-root", "recovery-root", "owner-receipt", "output-root"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    args.output_root.mkdir(parents=True, exist_ok=False)
    contracts.main()  # Actual full shared schema + valid/invalid fixture gate.
    registry, loaded = contracts.build_registry()
    checks = []

    def validate(schema, value, valid=True):
        schema = schema.resolve()
        wrapped = {"$ref": schema.as_uri()}
        errors = list(Draft202012Validator(wrapped, registry=registry, format_checker=FormatChecker()).iter_errors(value))
        if bool(errors) == valid:
            raise ValueError(str(schema) + ": " + (str(errors[0]) if errors else "negative unexpectedly accepted"))
        checks.append({"schema": str(schema), "expected": "valid" if valid else "invalid", "passed": True})

    folder = contracts.CORE_V3_READ_TO_ANKI
    legacy = read(args.test_root / "read-projection.v4.json")
    neutral = read(args.test_root / "read-projection.v5.json")
    validate(folder / "projection.v4.schema.json", legacy)
    validate(folder / "projection.v5.schema.json", neutral)
    validate(folder / "projection.v4.schema.json", neutral, False)
    altered = copy.deepcopy(neutral)
    altered["destinations"][0]["state"] = "unknown"
    altered["destinations"][0]["externalStateKnown"] = True
    validate(folder / "projection.v5.schema.json", altered, False)
    altered = copy.deepcopy(neutral)
    del altered["outputs"]["destinationSnapshotArtifact"]
    validate(folder / "projection.v5.schema.json", altered, False)
    adapter = read(args.adapter_root / "receipt.json")
    validate(contracts.ROOT / "contracts/remnote-target-discovery-request.v1.schema.json", read(adapter["publicRequest"]))
    # No remote resolution: verify every schema dependency can be read inside the repository.
    seen = set()
    def visit(path):
        path = path.resolve(strict=True)
        path.relative_to(contracts.ROOT)
        if path in seen:
            return
        seen.add(path)
        def walk(value):
            if isinstance(value, dict):
                if "$ref" in value:
                    file, _ = urldefrag(value["$ref"])
                    if file:
                        if ":" in file:
                            raise ValueError("Nonlocal schema dependency: " + file)
                        visit(path.parent / file)
                for child in value.values():
                    walk(child)
            elif isinstance(value, list):
                for child in value:
                    walk(child)
        walk(read(path))
    for schema in list(contracts.CORE_V3_DESTINATIONS.glob("*.schema.json")) + [folder / "projection.v5.schema.json"]:
        visit(schema)
    write_new(args.output_root / "schema-dependencies.json", {"portablePackagingRequired": True, "files": [evidence(p) for p in sorted(seen)]})
    source_paths = []
    for subtree in (contracts.ROOT / "core-v3/src", contracts.ROOT / "core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests"):
        source_paths += [p for p in subtree.rglob("*") if p.is_file() and not any(part in ("bin", "obj") for part in p.relative_to(subtree).parts) and p.suffix in (".cs", ".csproj", ".json")]
    source_paths += list(seen) + [Path(__file__).resolve(), Path(contracts.__file__).resolve()]
    sources = [evidence(p) for p in sorted(set(source_paths))]
    write_new(args.output_root / "source-binding.json", {"files": sources, "totalBytes": sum(x["bytes"] for x in sources)})
    tests = read(args.test_root / "receipt.json")
    if tests["status"] != "PASS" or adapter["status"] != "PASS" or not adapter["productionPublicHandler"]:
        raise ValueError("Required offline input failed")
    if adapter["workerRunInvocations"] != 0 or adapter["publicCommandInvocations"] != 0 or adapter["externalCalls"] != 0:
        raise ValueError("Offline boundary violated")
    recovery = args.recovery_root / "receipt.json"
    if read(recovery)["status"] != "PASS":
        raise ValueError("Previously accepted recovery receipt failed")
    inputs = [args.test_root / "receipt.json", args.adapter_root / "receipt.json", recovery, args.owner_receipt,
              args.output_root / "source-binding.json", args.output_root / "schema-dependencies.json"]
    inputs += sorted(args.build_root.glob("KnowledgeClipper.Core.V3.*.dll"))
    inputs += sorted(args.sdk_root.glob("KnowledgeClipper.Core.V3.*.dll"))
    inputs += [Path(adapter["request"]), Path(adapter["publicRequest"]), Path(adapter["probeRequest"])]
    receipt = {"schema": "kc.destination-offline-qualification/1", "status": "PASS-OFFLINE", "authorityBound": False, "candidatePromoted": False,
               "boundary": "actual-kernel-sdk-named-pipe + fake-worker-invoker + actual-remnote-describe + independent-module-parser",
               "handlerSdkAssertions": len(tests["cases"]), "runtimeFakeExports": adapter["fakeExportInvocations"], "runtimeFakePublic": adapter["fakePublicInvocations"],
               "actualDescribeInvocationsInAdapterRun": adapter["describeInvocations"], "recovery": "six previously accepted child-exit/reopen scenarios; not rerun",
               "schemaChecks": checks, "inputs": [evidence(p) for p in inputs], "externalCalls": 0, "workerRunInvocations": 0, "physicalAudio": 0,
               "remaining": ["Desktop actual gateway joint acceptance owned by Desktop", "sealed candidate/source-bound release packaging not promoted", "live D canary not authorized by this receipt"]}
    write_new(args.output_root / "receipt.json", receipt)
    lines = ["# Core optional card destinations — offline qualification", "", "PASS-OFFLINE; not a sealed product release.", "",
             "Input capture / question → one immutable destination snapshot → Content draft → explicit confirmation → independent Anki/RemNote results.",
             "Empty selections remain empty. Question successors retain initial targets. Confirmation does not call Content.",
             "Read-only discovery binds unique exact root evidence; settings/probe/restore do not grant writes.",
             "ConfigurationSnapshot is the entire destination snapshot. FrozenDestinationSummary.Configuration is the individual module config.",
             "RemNote cards use remnote.export + cards-remnote + card.bundle + payload v2 card-export, no dates or fake Anki config.",
             "Logical document count is distinct from unknown low-level mutation count. Unknown/started destinations never auto-retry or turn aggregate green.",
             "Neutral read query uses additive projection v5; closed legacy v4 is preserved. SDK04 supports neutral cancel without task.cancel.",
             "", f"Current handler/SDK assertions: {len(tests['cases'])}; runtime Fake export scenarios: {adapter['fakeExportInvocations']}; Fake public actions: {adapter['fakePublicInvocations']}.",
             "Actual worker executions, network/provider calls, destination writes, and physical audio: zero. Actual describe is counted separately.",
             "Historical invalid packages/build failures are retained; owner parser accepted only regenerated package adapter-offline-20260909-03.",
             "", "See receipt.json for exact source, runtime, SDK, schema dependencies, and independent receipt hashes."]
    with (args.output_root / "READABLE.md").open("x", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print(json.dumps({"status": "PASS-OFFLINE", "receipt": evidence(args.output_root / "receipt.json")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
