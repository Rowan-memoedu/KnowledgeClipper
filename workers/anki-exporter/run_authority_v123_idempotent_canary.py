#!/usr/bin/env python3
"""Run the exact authority-v1.2.3 Content -> Core -> Anki idempotent destination Canary."""

from __future__ import annotations

import run_authority_v122_idempotent_canary as canary


canary.AUTHORITY_VERSION = "1.2.3"
canary.AUTHORITY_SLUG = "v123"
canary.CONTENT_WORKER_VERSION = "0.5.2"
canary.ANKI_WORKER_VERSION = "1.1.5"
canary.AUTHORITY_ROOT = canary.PROJECT_ROOT / "contracts" / "authority" / "worker-registry" / "v1.2.3-20260828T021300Z"
canary.CHECKPOINT_PATH = canary.AUTHORITY_ROOT / "checkpoint.json"
canary.REGISTRY_PATH = canary.AUTHORITY_ROOT / "worker-registry.v1.json"
canary.DEPENDENCY_PATH = canary.AUTHORITY_ROOT / "dependency-manifest.v1.json"
canary.RELEASE_ROOT = canary.PROJECT_ROOT / "releases" / "knowledge-clipper-authority-v1.2.3-20260828T021300Z" / "win-x64"
canary.ORCHESTRATOR = canary.RELEASE_ROOT / "orchestrator" / "KnowledgeClipper.Orchestrator.exe"
canary.CONTENT_EXE = canary.RELEASE_ROOT / "workers" / "content" / "KnowledgeClipper.Content.Worker.exe"
canary.ANKI_EXE = canary.RELEASE_ROOT / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
canary.PROFILE_PATH = canary.RELEASE_ROOT / "contracts" / "core" / "v2.1" / "profile-resolver.v2.1.json"
canary.EXPECTED = {
    "checkpointSha256": "3EFB89D6D642F8546ABD14C5A114DE00E14BB7173EF33F03D3FD06DDAA2ADB55",
    "releaseFiles": 79,
    "releaseBytes": 248982362,
    "releaseTreeSha256": "7B27BACDB1DBB56991272972A5F9309B1D6F782787EB0B99EBD21248F7959574",
    "registrySha256": "B695939D797DDF7924D048B3380885ECF06AB926A807E9381D531855363F885B",
    "dependencySha256": "CA32D550B1E227332C9099E7D031779D92ACCD712F1B59AB1075721339471BDF",
    "orchestratorSha256": "FAE5F351719234B03C0F4FD58555AF4BA800E4E83FFCEFAB46B15C69923BDA9D",
    "contentSha256": "F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8",
    "ankiSha256": "4BDD812370D4BA45B45D86DF76A42F90D1CAD9A08D018810C9385BD99BB39774",
    "sourceCardBundleSha256": "E90E094EB52AF27D4C8BFA33ACF1F2BACC16A1917ADC6C674950E95539DAD0DF",
    "sourceReceiptSha256": "978EC4A0AC7E517EB6B71675C434BA3F63D7CE0FF4752AC17DC64DA12EA44851",
    "destinationReceiptSha256": "8D12EDE16B730BDEEEA1E8F094D179E18FA362ABB2BA834D8D0DF32D19C0C31C",
}


if __name__ == "__main__":
    raise SystemExit(canary.main())
