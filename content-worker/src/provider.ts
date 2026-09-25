import { execFile } from "node:child_process";
import { homedir } from "node:os";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { promisify } from "node:util";
import type { JsonObject } from "./types.js";
import { readJson } from "./util.js";

const execFileAsync = promisify(execFile);

export interface ProviderRequest {
  stage: "ocr_proofreading" | "ocr_lexical_audit" | "cleaning" | "classification" | "card_generation" | "language_writing" | "question_answer";
  callKey: string;
  system: string;
  payload: JsonObject;
  signal?: AbortSignal;
}

export interface ProviderResult {
  response: unknown;
  provider: string;
  model: string;
  mode: "replay" | "live";
  attempts: number;
}

export interface ContentProvider {
  call(request: ProviderRequest): Promise<ProviderResult>;
}

interface ReplayFile {
  schema_version: "provider-replay/1.0.0";
  responses: Record<string, unknown>;
}

export class ReplayProvider implements ContentProvider {
  private constructor(private readonly data: ReplayFile) {}

  static async fromFile(filePath: string): Promise<ReplayProvider> {
    const data = await readJson<ReplayFile>(filePath);
    if (data.schema_version !== "provider-replay/1.0.0" || !data.responses || typeof data.responses !== "object") throw new Error("REPLAY_INVALID");
    return new ReplayProvider(data);
  }

  async call(request: ProviderRequest): Promise<ProviderResult> {
    if (request.signal?.aborted) throw new Error("WORKER_CANCELLED");
    if (!(request.callKey in this.data.responses)) throw new Error(`REPLAY_MISSING ${request.callKey}`);
    const response = structuredClone(this.data.responses[request.callKey]);
    return { response, provider: "recorded", model: "fixture", mode: "replay", attempts: 1 };
  }
}

export class DeepSeekProvider implements ContentProvider {
  private constructor(
    private readonly apiKey: string,
    private readonly baseUrl: string,
    private readonly model: string,
    private readonly maxRetries: number,
  ) {}

  static async create(overrides?: { model?: "deepseek-chat" | "deepseek-reasoner"; maxRetries?: number }): Promise<DeepSeekProvider> {
    const apiKey = await discoverSetting("DEEPSEEK_KNOWLEDGECLIPPER_API_KEY");
    if (!apiKey) throw new Error("AI_AUTH missing DEEPSEEK_KNOWLEDGECLIPPER_API_KEY");
    const baseUrl = (await discoverSetting("AI_PRODUCER_BASE_URL")) ?? "https://api.deepseek.com";
    const model = overrides?.model ?? (await discoverSetting("AI_PRODUCER_MODEL")) ?? "deepseek-chat";
    const retryText = await discoverSetting("AI_PRODUCER_MAX_RETRIES");
    const parsedRetries = Number.parseInt(retryText ?? "3", 10);
    const configuredRetries = overrides?.maxRetries ?? (Number.isInteger(parsedRetries) && parsedRetries > 0 ? parsedRetries : 3);
    return new DeepSeekProvider(apiKey, baseUrl, model, configuredRetries);
  }

  async call(request: ProviderRequest): Promise<ProviderResult> {
    let lastError: Error | undefined;
    let lastKind: "AI_TRANSIENT" | "AI_CONTRACT" = "AI_TRANSIENT";
    for (let attempt = 1; attempt <= this.maxRetries; attempt += 1) {
      try {
        const controller = new AbortController();
        const relayAbort = () => controller.abort(request.signal?.reason);
        request.signal?.addEventListener("abort", relayAbort, { once: true });
        const timeout = setTimeout(() => controller.abort(new Error("AI_TRANSIENT request timeout")), 240_000);
        try {
          const response = await fetch(`${this.baseUrl.replace(/\/+$/u, "")}/chat/completions`, {
            method: "POST",
            headers: { Authorization: `Bearer ${this.apiKey}`, "Content-Type": "application/json" },
            body: JSON.stringify({
              model: this.model,
              messages: [
                { role: "system", content: request.system },
                { role: "user", content: `<INPUT_JSON>\n${JSON.stringify(request.payload)}\n</INPUT_JSON>` },
              ],
              response_format: { type: "json_object" },
              temperature: 0,
              max_tokens: 8192,
            }),
            signal: controller.signal,
          });
          const raw = await response.text();
          if (!response.ok) {
            const retryable = response.status === 429 || response.status >= 500;
            const error = new Error(`${retryable ? "AI_TRANSIENT" : "AI_AUTH"} DeepSeek HTTP ${response.status}`);
            if (!retryable) throw error;
            lastError = error;
            continue;
          }
          let root: { choices?: { message?: { content?: string } }[] };
          try { root = JSON.parse(raw) as typeof root; }
          catch (error) { throw new Error(`AI_CONTRACT invalid provider JSON: ${(error as Error).message}`); }
          const content = root.choices?.[0]?.message?.content;
          if (!content) throw new Error("AI_CONTRACT empty response");
          let parsed: unknown;
          try { parsed = JSON.parse(content) as unknown; }
          catch (error) { throw new Error(`AI_CONTRACT invalid content JSON: ${(error as Error).message}`); }
          return { response: parsed, provider: "deepseek", model: this.model, mode: "live", attempts: attempt };
        } finally {
          clearTimeout(timeout);
          request.signal?.removeEventListener("abort", relayAbort);
        }
      } catch (error) {
        if (request.signal?.aborted) throw new Error("WORKER_CANCELLED");
        const normalized = error instanceof Error ? error : new Error(String(error));
        if (normalized.message.startsWith("AI_AUTH")) throw normalized;
        lastKind = normalized.message.startsWith("AI_CONTRACT") ? "AI_CONTRACT" : "AI_TRANSIENT";
        lastError = normalized.message.startsWith("AI_") ? normalized : new Error(`AI_TRANSIENT ${normalized.message}`);
      }
    }
    throw new Error(`${lastKind} exhausted retries: ${lastError?.message ?? "unknown"}`);
  }
}

async function discoverSetting(name: string): Promise<string | undefined> {
  const inherited = process.env[name]?.trim();
  if (inherited) return inherited;
  if (process.platform === "win32") {
    try {
      const { stdout } = await execFileAsync("reg.exe", ["query", "HKCU\\Environment", "/v", name], { windowsHide: true, encoding: "utf8" });
      const match = stdout.match(new RegExp(`${escapeRegex(name)}\\s+REG_\\w+\\s+(.+)$`, "mi"));
      const value = match?.[1]?.trim();
      if (value) return value;
    } catch {
      // Continue to the explicit local configuration file.
    }
  }
  if (name === "DEEPSEEK_KNOWLEDGECLIPPER_API_KEY") return undefined;
  const configuredPath = process.env.AI_PRODUCER_CONFIG_FILE?.trim() || path.join(homedir(), "Documents", "New project", "DeepSeek API 配置.md");
  try {
    const content = await readFile(configuredPath, "utf8");
    const patterns = [
      new RegExp(`^\\s*${escapeRegex(name)}\\s*[=:]\\s*[\"']?([^\"'\\r\\n]+)`, "mi"),
      name === "DEEPSEEK_KNOWLEDGECLIPPER_API_KEY" ? /^\s*(?:DeepSeek\s*)?API\s*Key\s*[=:]\s*["']?([^"'\r\n]+)/mi : undefined,
    ].filter((pattern): pattern is RegExp => Boolean(pattern));
    for (const pattern of patterns) {
      const match = content.match(pattern);
      const value = match?.[1]?.trim();
      if (value) return value;
    }
  } catch {
    // Missing optional configuration file is reported by the caller when required.
  }
  return undefined;
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}
