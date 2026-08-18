import {
  normalizeHermesModelCatalog,
  type HermesModelCatalog,
  type HermesModelSelection,
} from "../HermesSettings/HermesModelAdapter";

export type HermesEnvironmentVariable = {
  key: string;
  provider: string;
  providerLabel: string;
  isSet: boolean;
  redactedValue: string;
  description: string;
  url: string;
  category: string;
  password: boolean;
  advanced: boolean;
};

export type HermesOAuthProvider = {
  id: string;
  name: string;
  flow: "pkce" | "device_code" | "external";
  cliCommand: string;
  docsUrl: string;
  disconnectCommand: string;
  disconnectHint: string;
  disconnectable: boolean;
  status: {
    loggedIn: boolean;
    source: string;
    sourceLabel: string;
    expiresAt: string;
    error: string;
  };
};

export type HermesOAuthStart = {
  sessionId: string;
  flow: "pkce" | "device_code";
  authUrl: string;
  verificationUrl: string;
  userCode: string;
  expiresIn: number;
  pollInterval: number;
};

export type HermesOAuthPoll = {
  sessionId: string;
  status: "pending" | "approved" | "denied" | "expired" | "error";
  errorMessage: string;
};

export type HermesCredentialPoolEntry = {
  provider: string;
  index: number;
  id: string;
  label: string;
  authType: string;
  source: string;
  priority: number;
  lastStatus: string;
  requestCount: number;
  tokenPreview: string;
  hasRefresh: boolean;
};

export type HermesAuxiliaryAssignment = HermesModelSelection & {
  task: string;
  baseUrl: string;
  apiMode: string;
  keyEnv: string;
  hasInlineApiKey: boolean;
};
export type HermesAuxiliarySnapshot = {
  main: HermesModelSelection;
  tasks: HermesAuxiliaryAssignment[];
};

export type HermesFallbackEntry = HermesModelSelection & {
  baseUrl?: string;
  apiMode?: string;
  keyEnv?: string;
  hasInlineApiKey?: boolean;
};
export type HermesFallbackSnapshot = {
  primary: HermesModelSelection;
  entries: HermesFallbackEntry[];
};

export type HermesModelDefaults = {
  reasoningEffort: string;
  reasoningOptions: string[];
  serviceTier: "normal" | "fast";
};

export type HermesProfile = {
  name: string;
  provider: string;
  model: string;
  isDefault: boolean;
};

export type HermesProfileSnapshot = {
  active: string;
  current: string;
  profiles: HermesProfile[];
};

export type HermesMoaSlot = HermesModelSelection & {
  reasoningEffort?: string;
  maxTokens?: number | null;
  enabled?: boolean;
};
export type HermesMoaPreset = {
  reference_models: HermesMoaSlot[];
  aggregator: HermesMoaSlot;
  reference_temperature: number | null;
  aggregator_temperature: number | null;
  reference_timeout: number | null;
  degraded_reference_policy: "loud" | "silent";
  max_tokens: number;
  reference_max_tokens?: number | null;
  fanout?: string;
  enabled: boolean;
};
export type HermesMoaConfig = HermesMoaPreset & {
  default_preset: string;
  active_preset: string;
  presets: Record<string, HermesMoaPreset>;
  privacyFilter: "" | "display" | "full";
  saveTraces: boolean;
  traceDir: string;
};

export type HermesModelAssignmentResult = {
  ok: boolean;
  confirmRequired: boolean;
  confirmMessage: string;
  staleAuxiliary: HermesAuxiliaryAssignment[];
};

const object = (value: unknown): Record<string, unknown> =>
  value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
const text = (value: unknown) =>
  typeof value === "string" ? value.trim() : "";
const finite = (value: unknown, fallback: number | null = null) =>
  typeof value === "number" && Number.isFinite(value) ? value : fallback;

async function json(url: string, init?: RequestInit) {
  const response = await fetch(url, { credentials: "include", ...init });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    const raw = object(payload);
    throw new Error(
      text(raw.detail) ||
        text(raw.error) ||
        `Hermes returned HTTP ${response.status}.`,
    );
  }
  return payload;
}

function normalizeSelection(value: unknown): HermesModelSelection {
  const raw = object(value);
  return { provider: text(raw.provider), model: text(raw.model) };
}

function normalizeMoaSlot(value: unknown): HermesMoaSlot {
  const raw = object(value);
  return {
    ...normalizeSelection(raw),
    ...(text(raw.reasoning_effort)
      ? { reasoningEffort: text(raw.reasoning_effort) }
      : {}),
    ...(raw.max_tokens === null || typeof raw.max_tokens === "number"
      ? { maxTokens: finite(raw.max_tokens) }
      : {}),
    ...(typeof raw.enabled === "boolean" ? { enabled: raw.enabled } : {}),
  };
}

function normalizeMoaPreset(value: unknown): HermesMoaPreset {
  const raw = object(value);
  return {
    reference_models: Array.isArray(raw.reference_models)
      ? raw.reference_models.map(normalizeMoaSlot)
      : [],
    aggregator: normalizeMoaSlot(raw.aggregator),
    reference_temperature: finite(raw.reference_temperature),
    aggregator_temperature: finite(raw.aggregator_temperature),
    reference_timeout: finite(raw.reference_timeout),
    degraded_reference_policy:
      raw.degraded_reference_policy === "silent" ? "silent" : "loud",
    max_tokens: finite(raw.max_tokens, 4096) ?? 4096,
    ...(raw.reference_max_tokens === null ||
    typeof raw.reference_max_tokens === "number"
      ? { reference_max_tokens: finite(raw.reference_max_tokens) }
      : {}),
    ...(text(raw.fanout) ? { fanout: text(raw.fanout) } : {}),
    enabled: raw.enabled !== false,
  };
}

function normalizeMoaConfig(value: unknown): HermesMoaConfig {
  const raw = object(value);
  const presets = Object.fromEntries(
    Object.entries(object(raw.presets)).map(([name, preset]) => [
      name,
      normalizeMoaPreset(preset),
    ]),
  );
  return {
    ...normalizeMoaPreset(raw),
    default_preset: text(raw.default_preset) || "default",
    active_preset:
      text(raw.active_preset) ||
      text(raw.default_preset) ||
      Object.keys(presets)[0] ||
      "default",
    presets,
    privacyFilter:
      raw.privacy_filter === "display" || raw.privacy_filter === "full"
        ? raw.privacy_filter
        : "",
    saveTraces: raw.save_traces === true,
    traceDir: text(raw.trace_dir),
  };
}

function serializeMoaSlot(slot: HermesMoaSlot) {
  return {
    provider: slot.provider.trim(),
    model: slot.model.trim(),
    ...(slot.reasoningEffort ? { reasoning_effort: slot.reasoningEffort } : {}),
    ...(typeof slot.maxTokens === "number" && Number.isFinite(slot.maxTokens)
      ? { max_tokens: slot.maxTokens }
      : {}),
    ...(typeof slot.enabled === "boolean" ? { enabled: slot.enabled } : {}),
  };
}

function serializeMoaPreset(preset: HermesMoaPreset) {
  return {
    reference_models: preset.reference_models.map(serializeMoaSlot),
    aggregator: serializeMoaSlot(preset.aggregator),
    reference_temperature: preset.reference_temperature,
    aggregator_temperature: preset.aggregator_temperature,
    reference_timeout: preset.reference_timeout,
    degraded_reference_policy: preset.degraded_reference_policy,
    max_tokens: preset.max_tokens,
    reference_max_tokens: preset.reference_max_tokens ?? null,
    fanout: preset.fanout ?? "user_turn",
    enabled: preset.enabled,
  };
}

export class HermesModelManagementAdapter {
  private profile = "";

  selectProfile(profile: string) {
    this.profile = profile.trim();
  }

  private endpoint(url: string) {
    if (!this.profile) return url;
    const separator = url.includes("?") ? "&" : "?";
    return `${url}${separator}profile=${encodeURIComponent(this.profile)}`;
  }

  async profiles(): Promise<HermesProfileSnapshot> {
    const [profilesPayload, activePayload] = await Promise.all([
      json("/api/profiles"),
      json("/api/profiles/active"),
    ]);
    const profilesRaw = object(profilesPayload);
    const activeRaw = object(activePayload);
    return {
      active: text(activeRaw.active) || "default",
      current: text(activeRaw.current) || "default",
      profiles: (Array.isArray(profilesRaw.profiles)
        ? profilesRaw.profiles
        : []
      ).flatMap((value) => {
        const profile = object(value);
        const name = text(profile.name);
        if (!name) return [];
        return [
          {
            name,
            provider: text(profile.provider),
            model: text(profile.model),
            isDefault: profile.is_default === true,
          },
        ];
      }),
    };
  }

  async modelCatalog(refresh = false): Promise<HermesModelCatalog> {
    const suffix = refresh ? "&refresh=1" : "";
    return normalizeHermesModelCatalog(
      await json(
        this.endpoint(`/api/model/options?include_unconfigured=1${suffix}`),
      ),
    );
  }

  async environment(): Promise<HermesEnvironmentVariable[]> {
    const raw = object(await json(this.endpoint("/api/env")));
    return Object.entries(raw).flatMap(([key, value]) => {
      const item = object(value);
      if (!key || !text(item.category)) return [];
      return [
        {
          key,
          provider: text(item.provider),
          providerLabel: text(item.provider_label),
          isSet: item.is_set === true,
          redactedValue: text(item.redacted_value),
          description: text(item.description),
          url: text(item.url),
          category: text(item.category),
          password: item.is_password === true,
          advanced: item.advanced === true,
        },
      ];
    });
  }

  async setEnvironment(key: string, value: string) {
    await json(this.endpoint("/api/env"), {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ key, value }),
    });
  }

  async clearEnvironment(key: string) {
    await json(this.endpoint("/api/env"), {
      method: "DELETE",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ key }),
    });
  }

  async oauthProviders(): Promise<HermesOAuthProvider[]> {
    const raw = object(await json(this.endpoint("/api/providers/oauth")));
    return (Array.isArray(raw.providers) ? raw.providers : []).flatMap(
      (value) => {
        const provider = object(value);
        const flow =
          provider.flow === "pkce" ||
          provider.flow === "device_code" ||
          provider.flow === "external"
            ? provider.flow
            : null;
        const id = text(provider.id);
        if (!id || !flow) return [];
        const status = object(provider.status);
        return [
          {
            id,
            name: text(provider.name) || id,
            flow,
            cliCommand: text(provider.cli_command),
            docsUrl: text(provider.docs_url),
            disconnectCommand: text(provider.disconnect_command),
            disconnectHint: text(provider.disconnect_hint),
            disconnectable: provider.disconnectable === true,
            status: {
              loggedIn: status.logged_in === true,
              source: text(status.source),
              sourceLabel: text(status.source_label),
              expiresAt: text(status.expires_at),
              error: text(status.error),
            },
          },
        ];
      },
    );
  }

  async startOAuth(providerId: string): Promise<HermesOAuthStart> {
    const raw = object(
      await json(
        this.endpoint(
          `/api/providers/oauth/${encodeURIComponent(providerId)}/start`,
        ),
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: "{}",
        },
      ),
    );
    const flow =
      raw.flow === "pkce" || raw.flow === "device_code" ? raw.flow : null;
    const sessionId = text(raw.session_id);
    if (!flow || !sessionId)
      throw new Error("Hermes returned an invalid authentication session.");
    return {
      sessionId,
      flow,
      authUrl: text(raw.auth_url),
      verificationUrl: text(raw.verification_url),
      userCode: text(raw.user_code),
      expiresIn: finite(raw.expires_in, 600) ?? 600,
      pollInterval: finite(raw.poll_interval, 3) ?? 3,
    };
  }

  async submitOAuthCode(providerId: string, sessionId: string, code: string) {
    await json(
      this.endpoint(
        `/api/providers/oauth/${encodeURIComponent(providerId)}/submit`,
      ),
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ session_id: sessionId, code }),
      },
    );
  }

  async pollOAuth(
    providerId: string,
    sessionId: string,
  ): Promise<HermesOAuthPoll> {
    const raw = object(
      await json(
        this.endpoint(
          `/api/providers/oauth/${encodeURIComponent(providerId)}/poll/${encodeURIComponent(sessionId)}`,
        ),
      ),
    );
    const status =
      raw.status === "approved" ||
      raw.status === "denied" ||
      raw.status === "expired" ||
      raw.status === "error"
        ? raw.status
        : "pending";
    return {
      sessionId: text(raw.session_id) || sessionId,
      status,
      errorMessage: text(raw.error_message),
    };
  }

  async cancelOAuth(sessionId: string) {
    await json(
      `/api/providers/oauth/sessions/${encodeURIComponent(sessionId)}`,
      { method: "DELETE" },
    );
  }

  async disconnectOAuth(providerId: string) {
    await json(
      this.endpoint(`/api/providers/oauth/${encodeURIComponent(providerId)}`),
      { method: "DELETE" },
    );
  }

  async credentialPool(): Promise<HermesCredentialPoolEntry[]> {
    const raw = object(await json(this.endpoint("/api/credentials/pool")));
    return (Array.isArray(raw.providers) ? raw.providers : []).flatMap(
      (groupValue) => {
        const group = object(groupValue);
        const provider = text(group.provider);
        return (Array.isArray(group.entries) ? group.entries : []).flatMap(
          (entryValue) => {
            const entry = object(entryValue);
            const index = finite(entry.index, 0) ?? 0;
            if (!provider || index < 1) return [];
            return [
              {
                provider,
                index,
                id: text(entry.id),
                label: text(entry.label),
                authType: text(entry.auth_type),
                source: text(entry.source),
                priority: finite(entry.priority, 0) ?? 0,
                lastStatus: text(entry.last_status),
                requestCount: finite(entry.request_count, 0) ?? 0,
                tokenPreview: text(entry.token_preview),
                hasRefresh: entry.has_refresh === true,
              },
            ];
          },
        );
      },
    );
  }

  async addCredentialPoolEntry(provider: string, apiKey: string, label = "") {
    await json(this.endpoint("/api/credentials/pool"), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ provider, api_key: apiKey, label }),
    });
  }

  async removeCredentialPoolEntry(provider: string, index: number) {
    await json(
      this.endpoint(
        `/api/credentials/pool/${encodeURIComponent(provider)}/${index}`,
      ),
      { method: "DELETE" },
    );
  }

  async auxiliary(): Promise<HermesAuxiliarySnapshot> {
    const raw = object(await json(this.endpoint("/api/model/auxiliary")));
    return {
      main: normalizeSelection(raw.main),
      tasks: (Array.isArray(raw.tasks) ? raw.tasks : [])
        .map((value) => ({
          ...normalizeSelection(value),
          task: text(object(value).task),
          baseUrl: text(object(value).base_url),
          apiMode: text(object(value).api_mode),
          keyEnv: text(object(value).key_env),
          hasInlineApiKey: object(value).has_inline_api_key === true,
        }))
        .filter((value) => value.task),
    };
  }

  async setAssignment(
    scope: "main" | "auxiliary",
    selection: HermesModelSelection,
    options: {
      task?: string;
      confirmExpensiveModel?: boolean;
      baseUrl?: string;
      apiMode?: string;
      keyEnv?: string;
    } = {},
  ): Promise<HermesModelAssignmentResult> {
    const raw = object(
      await json(this.endpoint("/api/model/set"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          scope,
          provider: selection.provider,
          model: selection.model,
          task: options.task ?? "",
          base_url: options.baseUrl ?? "",
          api_mode: options.apiMode ?? "",
          key_env: options.keyEnv ?? "",
          confirm_expensive_model: options.confirmExpensiveModel === true,
        }),
      }),
    );
    return {
      ok: raw.ok === true,
      confirmRequired: raw.confirm_required === true,
      confirmMessage: text(raw.confirm_message),
      staleAuxiliary: (Array.isArray(raw.stale_aux) ? raw.stale_aux : [])
        .map((value) => ({
          ...normalizeSelection(value),
          task: text(object(value).task),
          baseUrl: "",
          apiMode: "",
          keyEnv: "",
          hasInlineApiKey: false,
        }))
        .filter((value) => value.task),
    };
  }

  async fallback(): Promise<HermesFallbackSnapshot> {
    const raw = object(await json(this.endpoint("/api/model/fallback")));
    return {
      primary: normalizeSelection(raw.primary),
      entries: (Array.isArray(raw.entries) ? raw.entries : [])
        .map((value) => {
          const item = object(value);
          return {
            ...normalizeSelection(item),
            ...(text(item.base_url) ? { baseUrl: text(item.base_url) } : {}),
            ...(text(item.api_mode) ? { apiMode: text(item.api_mode) } : {}),
            ...(text(item.key_env) ? { keyEnv: text(item.key_env) } : {}),
            ...(item.has_inline_api_key === true
              ? { hasInlineApiKey: true }
              : {}),
          };
        })
        .filter((value) => value.provider && value.model),
    };
  }

  async saveFallback(
    entries: HermesFallbackEntry[],
  ): Promise<HermesFallbackSnapshot> {
    const raw = object(
      await json(this.endpoint("/api/model/fallback"), {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          entries: entries.map((entry) => ({
            provider: entry.provider,
            model: entry.model,
            base_url: entry.baseUrl ?? "",
            api_mode: entry.apiMode ?? "",
            key_env: entry.keyEnv ?? "",
          })),
        }),
      }),
    );
    return {
      primary: normalizeSelection(raw.primary),
      entries: (Array.isArray(raw.entries) ? raw.entries : []).map((value) => {
        const item = object(value);
        return {
          ...normalizeSelection(item),
          ...(text(item.base_url) ? { baseUrl: text(item.base_url) } : {}),
          ...(text(item.api_mode) ? { apiMode: text(item.api_mode) } : {}),
          ...(text(item.key_env) ? { keyEnv: text(item.key_env) } : {}),
          ...(item.has_inline_api_key === true
            ? { hasInlineApiKey: true }
            : {}),
        };
      }),
    };
  }

  async moa(): Promise<HermesMoaConfig> {
    return normalizeMoaConfig(await json(this.endpoint("/api/model/moa")));
  }

  async saveMoa(config: HermesMoaConfig): Promise<HermesMoaConfig> {
    const raw = await json(this.endpoint("/api/model/moa"), {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        default_preset: config.default_preset,
        active_preset: config.active_preset,
        presets: Object.fromEntries(
          Object.entries(config.presets).map(([name, preset]) => [
            name,
            serializeMoaPreset(preset),
          ]),
        ),
        privacy_filter: config.privacyFilter,
        save_traces: config.saveTraces,
        trace_dir: config.traceDir,
      }),
    });
    return normalizeMoaConfig(raw);
  }

  async modelDefaults(): Promise<HermesModelDefaults> {
    const raw = object(await json(this.endpoint("/api/model/defaults")));
    return {
      reasoningEffort: text(raw.reasoning_effort) || "medium",
      reasoningOptions: (Array.isArray(raw.reasoning_options)
        ? raw.reasoning_options
        : []
      )
        .map(text)
        .filter(Boolean),
      serviceTier: raw.service_tier === "fast" ? "fast" : "normal",
    };
  }

  async saveModelDefaults(
    defaults: HermesModelDefaults,
  ): Promise<HermesModelDefaults> {
    const raw = object(
      await json(this.endpoint("/api/model/defaults"), {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          reasoning_effort: defaults.reasoningEffort,
          service_tier: defaults.serviceTier,
        }),
      }),
    );
    return {
      reasoningEffort: text(raw.reasoning_effort) || defaults.reasoningEffort,
      reasoningOptions: (Array.isArray(raw.reasoning_options)
        ? raw.reasoning_options
        : defaults.reasoningOptions
      )
        .map(text)
        .filter(Boolean),
      serviceTier: raw.service_tier === "fast" ? "fast" : "normal",
    };
  }
}

export const hermesModelManagementAdapter = new HermesModelManagementAdapter();
