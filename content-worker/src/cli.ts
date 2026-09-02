#!/usr/bin/env node
import path from "node:path";
import { fileURLToPath } from "node:url";
import { runCommonRequest, WORKER_DESCRIBE } from "./common-adapter.js";
import { copyBuiltInSettings, describeContentCompanion, describeContentSettings, diffContentSettingsDraft, validateContentCompanionSettings, validateContentSettingsDraft } from "./settings-adapter.js";

async function main(): Promise<void> {
  const values = process.argv.slice(2);
  const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
  if (values.length === 2 && values[0] === "describe" && values[1] === "--json") {
    process.stdout.write(`${JSON.stringify(WORKER_DESCRIBE)}\n`);
    return;
  }
  if (values.length === 3 && values[0] === "settings" && values[1] === "describe" && values[2] === "--json") {
    process.stdout.write(`${JSON.stringify(await describeContentSettings(workerRoot))}\n`);
    return;
  }
  if (values.length === 3 && values[0] === "settings" && values[1] === "companion" && values[2] === "--json") {
    process.stdout.write(`${JSON.stringify(await describeContentCompanion(workerRoot))}\n`);
    return;
  }
  if (values.length === 6 && values[0] === "settings" && values[1] === "profile" && values[2] === "validate" && values[3] === "--path" && values[5] === "--json") {
    process.stdout.write(`${JSON.stringify(await validateContentCompanionSettings(values[4]!))}\n`);
    return;
  }
  if (values.length === 5 && values[0] === "settings" && values[4] === "--json" && values[2] === "--target" && values[1] === "copy") {
    process.stdout.write(`${JSON.stringify(await copyBuiltInSettings(workerRoot, values[3]!))}\n`);
    return;
  }
  if (values.length === 5 && values[0] === "settings" && values[4] === "--json" && values[2] === "--root" && values[1] === "validate") {
    process.stdout.write(`${JSON.stringify(await validateContentSettingsDraft(workerRoot, values[3]!))}\n`);
    return;
  }
  if (values.length === 5 && values[0] === "settings" && values[4] === "--json" && values[2] === "--root" && values[1] === "diff") {
    process.stdout.write(`${JSON.stringify(await diffContentSettingsDraft(workerRoot, values[3]!))}\n`);
    return;
  }
  if (values.length !== 3 || values[0] !== "run" || values[1] !== "--request") throw new Error("CLI_INVALID expected describe --json, settings <describe|companion|copy|validate|diff|profile validate>, or run --request <absolute-request.json>");
  const requestPath = values[2]!;
  if (!path.isAbsolute(requestPath)) throw new Error("CLI_INVALID --request must be absolute");
  await runCommonRequest(requestPath, workerRoot);
}

main().catch((error: unknown) => {
  const message = error instanceof Error ? error.message : String(error);
  process.stderr.write(`${message}\n`);
  process.exitCode = 2;
});
