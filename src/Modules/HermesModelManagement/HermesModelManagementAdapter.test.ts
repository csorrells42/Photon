import { afterEach, describe, expect, it, vi } from "vitest";
import { HermesModelManagementAdapter } from "./HermesModelManagementAdapter";

const response = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });

const hermesProviderVariations = [
  "nous",
  "fireworks",
  "openrouter",
  "moa",
  "novita",
  "lmstudio",
  "anthropic",
  "openai-codex",
  "openai-api",
  "alibaba",
  "xai-oauth",
  "xiaomi",
  "tencent-tokenhub",
  "nvidia",
  "copilot",
  "copilot-acp",
  "huggingface",
  "gemini",
  "vertex",
  "deepseek",
  "xai",
  "zai",
  "kimi-coding",
  "kimi-coding-cn",
  "stepfun",
  "minimax",
  "minimax-oauth",
  "minimax-cn",
  "ollama-cloud",
  "arcee",
  "gmi",
  "kilocode",
  "opencode-zen",
  "opencode-go",
  "bedrock",
  "azure-foundry",
  "ai-gateway",
  "qwen-oauth",
  "actual",
  "alibaba-coding-plan",
  "custom",
  "deepinfra",
  "upstage",
] as const;

afterEach(() => vi.unstubAllGlobals());

describe("Hermes model management adapter", () => {
  it("normalizes every provider returned by Hermes without a Photon allowlist", async () => {
    const providers = hermesProviderVariations.map((slug) => ({
      slug,
      name: slug,
      authenticated: slug !== "vertex",
      models: [`${slug}/model`],
      featured_models: [],
      unavailable_models: [],
      capabilities: {},
    }));
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        response({ provider: "openai-codex", model: "gpt-5.6-sol", providers }),
      ),
    );

    const catalog = await new HermesModelManagementAdapter().modelCatalog(true);

    expect(catalog.providers).toHaveLength(43);
    expect(catalog.providers.map((provider) => provider.slug)).toEqual([
      ...hermesProviderVariations,
    ]);
    expect(fetch).toHaveBeenCalledWith(
      "/api/model/options?include_unconfigured=1&refresh=1",
      expect.objectContaining({ credentials: "include" }),
    );
  });

  it("never returns credential values while mapping the Hermes environment catalog", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        response({
          OPENAI_API_KEY: {
            is_set: true,
            redacted_value: "sk-…last4",
            category: "provider",
            provider: "openai-api",
            provider_label: "OpenAI API",
            is_password: true,
            description: "OpenAI",
            url: "https://platform.openai.com",
          },
        }),
      ),
    );

    const variables = await new HermesModelManagementAdapter().environment();

    expect(variables).toEqual([
      {
        key: "OPENAI_API_KEY",
        provider: "openai-api",
        providerLabel: "OpenAI API",
        isSet: true,
        redactedValue: "sk-…last4",
        category: "provider",
        password: true,
        advanced: false,
        description: "OpenAI",
        url: "https://platform.openai.com",
      },
    ]);
    expect(JSON.stringify(variables)).not.toContain("real-secret");
  });

  it("preserves ordered fallback routes and strips no routing metadata", async () => {
    const fetcher = vi.fn(
      async (_input: RequestInfo | URL, init?: RequestInit) => {
        const sent = JSON.parse(String(init?.body));
        return response({
          primary: { provider: "openai-codex", model: "gpt-5.6-sol" },
          entries: sent.entries,
        });
      },
    );
    vi.stubGlobal("fetch", fetcher);

    const result = await new HermesModelManagementAdapter().saveFallback([
      {
        provider: "openrouter",
        model: "anthropic/claude-opus",
        apiMode: "responses",
      },
      {
        provider: "custom",
        model: "qwen3-4b",
        baseUrl: "http://host.docker.internal:1234/v1",
        keyEnv: "LM_API_KEY",
      },
    ]);

    expect(result.entries).toEqual([
      {
        provider: "openrouter",
        model: "anthropic/claude-opus",
        apiMode: "responses",
      },
      {
        provider: "custom",
        model: "qwen3-4b",
        baseUrl: "http://host.docker.internal:1234/v1",
        keyEnv: "LM_API_KEY",
      },
    ]);
    expect(fetcher).toHaveBeenCalledWith(
      "/api/model/fallback",
      expect.objectContaining({ method: "PUT", credentials: "include" }),
    );
  });

  it("round-trips every Hermes MoA variation without stripping advisor or privacy controls", async () => {
    const raw = {
      default_preset: "review",
      active_preset: "review",
      presets: {
        review: {
          reference_models: [
            {
              provider: "openai-codex",
              model: "gpt-5.6-sol",
              reasoning_effort: "high",
              max_tokens: 640,
              enabled: true,
            },
          ],
          aggregator: {
            provider: "gemini",
            model: "gemini-3-pro",
            reasoning_effort: "medium",
          },
          reference_temperature: null,
          aggregator_temperature: 0.2,
          reference_timeout: 300,
          degraded_reference_policy: "loud",
          max_tokens: 4096,
          reference_max_tokens: 900,
          fanout: "every_n:3",
          enabled: true,
        },
      },
      privacy_filter: "full",
      save_traces: true,
      trace_dir: "/opt/data/moa-traces",
    };
    let sent: Record<string, unknown> = {};
    const fetcher = vi.fn(
      async (_input: RequestInfo | URL, init?: RequestInit) => {
        if (init?.method === "PUT") {
          sent = JSON.parse(String(init.body)) as Record<string, unknown>;
          return response(sent);
        }
        return response(raw);
      },
    );
    vi.stubGlobal("fetch", fetcher);
    const adapter = new HermesModelManagementAdapter();

    const config = await adapter.moa();
    const saved = await adapter.saveMoa(config);

    expect(config.presets.review.reference_models[0]).toMatchObject({
      reasoningEffort: "high",
      maxTokens: 640,
      enabled: true,
    });
    expect(config).toMatchObject({
      privacyFilter: "full",
      saveTraces: true,
      traceDir: "/opt/data/moa-traces",
    });
    expect(sent).toMatchObject({
      privacy_filter: "full",
      save_traces: true,
      trace_dir: "/opt/data/moa-traces",
    });
    expect(
      (
        sent.presets as Record<
          string,
          { reference_models: Array<Record<string, unknown>> }
        >
      ).review.reference_models[0],
    ).toMatchObject({
      reasoning_effort: "high",
      max_tokens: 640,
      enabled: true,
    });
    expect(saved.presets.review.fanout).toBe("every_n:3");
  });

  it("maps credential pools using only Hermes redacted summaries", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        response({
          providers: [
            {
              provider: "openai-codex",
              entries: [
                {
                  index: 1,
                  id: "acct-a",
                  label: "ChatGPT Plus",
                  auth_type: "oauth",
                  source: "device_code",
                  priority: 2,
                  last_status: "ready",
                  request_count: 17,
                  token_preview: "ey…42",
                  has_refresh: true,
                },
              ],
            },
          ],
        }),
      ),
    );

    const entries = await new HermesModelManagementAdapter().credentialPool();

    expect(entries).toEqual([
      {
        provider: "openai-codex",
        index: 1,
        id: "acct-a",
        label: "ChatGPT Plus",
        authType: "oauth",
        source: "device_code",
        priority: 2,
        lastStatus: "ready",
        requestCount: 17,
        tokenPreview: "ey…42",
        hasRefresh: true,
      },
    ]);
    expect(JSON.stringify(entries)).not.toContain("access_token");
  });

  it("scopes model surfaces to the selected Hermes profile", async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/profiles")
        return response({
          profiles: [
            { name: "default", is_default: true },
            { name: "worker beta", provider: "anthropic", model: "claude" },
          ],
        });
      if (url === "/api/profiles/active")
        return response({ active: "default", current: "worker beta" });
      if (url.startsWith("/api/model/defaults"))
        return response({
          reasoning_effort: "high",
          reasoning_options: ["none", "high"],
          service_tier: "fast",
        });
      return response({
        provider: "anthropic",
        model: "claude",
        providers: [],
      });
    });
    vi.stubGlobal("fetch", fetcher);
    const adapter = new HermesModelManagementAdapter();

    const snapshot = await adapter.profiles();
    adapter.selectProfile(snapshot.current);
    await adapter.modelCatalog(true);
    const defaults = await adapter.modelDefaults();

    expect(snapshot.current).toBe("worker beta");
    expect(snapshot.profiles[1]).toEqual({
      name: "worker beta",
      provider: "anthropic",
      model: "claude",
      isDefault: false,
    });
    expect(defaults).toEqual({
      reasoningEffort: "high",
      reasoningOptions: ["none", "high"],
      serviceTier: "fast",
    });
    expect(fetcher).toHaveBeenCalledWith(
      "/api/model/options?include_unconfigured=1&refresh=1&profile=worker%20beta",
      expect.objectContaining({ credentials: "include" }),
    );
    expect(fetcher).toHaveBeenCalledWith(
      "/api/model/defaults?profile=worker%20beta",
      expect.objectContaining({ credentials: "include" }),
    );
  });

  it("persists only exact supported reasoning and service-tier defaults", async () => {
    const fetcher = vi.fn(
      async (_input: RequestInfo | URL, init?: RequestInit) => {
        const sent = JSON.parse(String(init?.body));
        return response({
          ...sent,
          reasoning_options: [
            "none",
            "minimal",
            "low",
            "medium",
            "high",
            "xhigh",
            "max",
            "ultra",
          ],
        });
      },
    );
    vi.stubGlobal("fetch", fetcher);
    const adapter = new HermesModelManagementAdapter();

    const saved = await adapter.saveModelDefaults({
      reasoningEffort: "ultra",
      reasoningOptions: [
        "none",
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh",
        "max",
        "ultra",
      ],
      serviceTier: "normal",
    });

    expect(saved.reasoningEffort).toBe("ultra");
    expect(saved.serviceTier).toBe("normal");
    expect(fetcher).toHaveBeenCalledWith(
      "/api/model/defaults",
      expect.objectContaining({
        method: "PUT",
        credentials: "include",
        body: JSON.stringify({
          reasoning_effort: "ultra",
          service_tier: "normal",
        }),
      }),
    );
  });
});
