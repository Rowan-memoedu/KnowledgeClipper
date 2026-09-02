import assert from "node:assert/strict";
import test from "node:test";
import { DeepSeekProvider } from "../src/provider.js";

const REQUEST = {
  stage: "cleaning" as const,
  callKey: "cleaning:provider-test",
  system: "Return JSON.",
  payload: { source_text: "fixture" },
};

test("DeepSeek provider retries a transient HTTP failure and reports the successful attempt", async () => {
  await withFakeProvider(async () => {
    let calls = 0;
    globalThis.fetch = async () => {
      calls += 1;
      if (calls === 1) return new Response("temporary unavailable", { status: 503 });
      return jsonResponse({ choices: [{ message: { content: JSON.stringify({ cleaned_text: "fixture" }) } }] });
    };
    const provider = await DeepSeekProvider.create();
    const result = await provider.call(REQUEST);
    assert.equal(calls, 2);
    assert.equal(result.attempts, 2);
    assert.equal(result.provider, "deepseek");
    assert.equal(result.mode, "live");
    assert.deepEqual(result.response, { cleaned_text: "fixture" });
  });
});

test("DeepSeek provider does not retry an authentication failure", async () => {
  await withFakeProvider(async () => {
    let calls = 0;
    globalThis.fetch = async () => {
      calls += 1;
      return new Response("unauthorized", { status: 401 });
    };
    const provider = await DeepSeekProvider.create();
    await assert.rejects(() => provider.call(REQUEST), /AI_AUTH DeepSeek HTTP 401/u);
    assert.equal(calls, 1);
  });
});

test("DeepSeek provider fails as AI_CONTRACT after invalid JSON exhausts retries", async () => {
  await withFakeProvider(async () => {
    let calls = 0;
    globalThis.fetch = async () => {
      calls += 1;
      return new Response("not-json", { status: 200, headers: { "Content-Type": "application/json" } });
    };
    const provider = await DeepSeekProvider.create();
    await assert.rejects(
      () => provider.call(REQUEST),
      /AI_CONTRACT exhausted retries: AI_CONTRACT invalid provider JSON/u,
    );
    assert.equal(calls, 2);
  });
});

test("snapshot-bound provider overrides take precedence over local model and retry defaults", async () => {
  await withFakeProvider(async () => {
    let calls = 0;
    let requestedModel = "";
    globalThis.fetch = async (_input, init) => {
      calls += 1;
      requestedModel = (JSON.parse(String(init?.body)) as { model: string }).model;
      return new Response("temporary unavailable", { status: 503 });
    };
    const provider = await DeepSeekProvider.create({ model: "deepseek-reasoner", maxRetries: 1 });
    await assert.rejects(() => provider.call(REQUEST), /AI_TRANSIENT exhausted retries/u);
    assert.equal(calls, 1);
    assert.equal(requestedModel, "deepseek-reasoner");
  });
});

async function withFakeProvider(run: () => Promise<void>): Promise<void> {
  const originalFetch = globalThis.fetch;
  const original = {
    key: process.env.AI_PRODUCER_API_KEY,
    baseUrl: process.env.AI_PRODUCER_BASE_URL,
    model: process.env.AI_PRODUCER_MODEL,
    retries: process.env.AI_PRODUCER_MAX_RETRIES,
  };
  process.env.AI_PRODUCER_API_KEY = "test-key-not-a-secret";
  process.env.AI_PRODUCER_BASE_URL = "https://provider.test.invalid";
  process.env.AI_PRODUCER_MODEL = "test-model";
  process.env.AI_PRODUCER_MAX_RETRIES = "2";
  try {
    await run();
  } finally {
    globalThis.fetch = originalFetch;
    restoreEnv("AI_PRODUCER_API_KEY", original.key);
    restoreEnv("AI_PRODUCER_BASE_URL", original.baseUrl);
    restoreEnv("AI_PRODUCER_MODEL", original.model);
    restoreEnv("AI_PRODUCER_MAX_RETRIES", original.retries);
  }
}

function restoreEnv(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

function jsonResponse(value: unknown): Response {
  return new Response(JSON.stringify(value), { status: 200, headers: { "Content-Type": "application/json" } });
}
