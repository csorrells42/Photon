export const HERMES_RUNTIME_CONFIGURATION_VERSION = 1 as const;

export type HermesRuntimeApiMode =
  "" | "chat_completions" | "codex_responses" | "anthropic_messages";

export type HermesRuntimeProfileOverrides = Partial<{
  frequencyPenalty: number;
  maxTokens: number;
  minP: number;
  presencePenalty: number;
  repeatPenalty: number;
  seed: number;
  stop: string[];
  temperature: number;
  topK: number;
  topP: number;
}>;

export type HermesRuntimeProfile = {
  id: string;
  isActive: boolean;
  model: string;
  name: string;
  overrides: HermesRuntimeProfileOverrides;
};

export type HermesRuntimeProfileDraft = {
  id: string;
  makeActive: boolean;
  model: string;
  name: string;
  overrides: HermesRuntimeProfileOverrides;
};

export type HermesRuntimeModelDetail = {
  architecture?: string;
  capabilities?: {
    reasoning?: { allowedOptions: string[]; default?: string };
    trainedForToolUse?: boolean;
    vision?: boolean;
  };
  displayName?: string;
  format?: string;
  id: string;
  loaded: boolean;
  loadedInstances: Array<{
    id: string;
    config: Record<string, boolean | number | string>;
  }>;
  maxContextLength?: number;
  parameters?: string;
  publisher?: string;
  quantization?: { bitsPerWeight?: number; name?: string };
  selectedVariant?: string;
  sizeBytes?: number;
  type: string;
};

export type HermesRuntimeEndpoint = {
  apiMode: HermesRuntimeApiMode;
  baseUrl: string;
  contextLength?: number;
  discoverModels: boolean;
  hasCredential: boolean;
  id: string;
  isCurrent: boolean;
  model: string;
  models: string[];
  name: string;
  externalOverridesPresent: boolean;
  profiles: HermesRuntimeProfile[];
  source: "providers" | "direct-config";
};

export type HermesRuntimeEndpointSnapshot = {
  current: {
    apiMode: HermesRuntimeApiMode;
    baseUrl: string;
    model: string;
    provider: string;
  };
  endpoints: HermesRuntimeEndpoint[];
};

export type HermesRuntimeEndpointDraft = {
  apiMode: HermesRuntimeApiMode;
  baseUrl: string;
  contextLength?: number;
  discoverModels: boolean;
  id?: string;
  makeDefault: boolean;
  model: string;
  models?: string[];
  name: string;
};

export type HermesRuntimeEndpointValidation = {
  message: string;
  modelDetails: HermesRuntimeModelDetail[];
  models: string[];
  ok: boolean;
  reachable: boolean;
  runtimeKind: "lm-studio" | "openai-compatible" | "unknown";
};

const identifierPattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$/;

function normalizeApiMode(value: unknown): HermesRuntimeApiMode {
  return value === "chat_completions" ||
    value === "codex_responses" ||
    value === "anthropic_messages"
    ? value
    : "";
}

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
}

function boundedText(value: unknown, maximum: number): string {
  if (typeof value !== "string") return "";
  const result = value.trim();
  return result.length <= maximum ? result : "";
}

function boundedStrings(value: unknown, maximumItems = 2_000): string[] {
  if (!Array.isArray(value) || value.length > maximumItems) return [];
  return [
    ...new Set(value.map((item) => boundedText(item, 512)).filter(Boolean)),
  ];
}

function optionalNumber(
  value: unknown,
  minimum: number,
  maximum: number,
): number | undefined {
  return typeof value === "number" &&
    Number.isFinite(value) &&
    value >= minimum &&
    value <= maximum
    ? value
    : undefined;
}

function normalizeProfileOverrides(
  value: unknown,
): HermesRuntimeProfileOverrides {
  const raw = record(value);
  const result: HermesRuntimeProfileOverrides = {};
  const mappings = [
    ["temperature", "temperature", -100, 100],
    ["top_p", "topP", 0, 1],
    ["frequency_penalty", "frequencyPenalty", -100, 100],
    ["presence_penalty", "presencePenalty", -100, 100],
    ["top_k", "topK", 0, 1_000_000],
    ["min_p", "minP", 0, 1],
    ["repeat_penalty", "repeatPenalty", 0, 100],
    ["max_tokens", "maxTokens", 1, 16_777_216],
    ["seed", "seed", -2_147_483_648, 2_147_483_647],
  ] as const;
  for (const [source, target, minimum, maximum] of mappings) {
    const number = optionalNumber(raw[source], minimum, maximum);
    if (number !== undefined) result[target] = number;
  }
  const stop = boundedStrings(raw.stop, 8).filter((item) => item.length <= 256);
  if (stop.length) result.stop = stop;
  return result;
}

function normalizeProfiles(value: unknown): HermesRuntimeProfile[] {
  if (!Array.isArray(value) || value.length > 256) return [];
  return value.flatMap((candidate): HermesRuntimeProfile[] => {
    const raw = record(candidate);
    const id = boundedText(raw.id, 96);
    const name = boundedText(raw.name, 120);
    const model = boundedText(raw.model, 512);
    if (!identifierPattern.test(id) || !name || !model) return [];
    return [
      {
        id,
        name,
        model,
        isActive: raw.is_active === true,
        overrides: normalizeProfileOverrides(raw.overrides),
      },
    ];
  });
}

function normalizeModelDetails(value: unknown): HermesRuntimeModelDetail[] {
  if (!Array.isArray(value) || value.length > 2_000) return [];
  return value.flatMap((candidate): HermesRuntimeModelDetail[] => {
    const raw = record(candidate);
    const id = boundedText(raw.id, 512);
    const type = boundedText(raw.type, 64);
    if (!id || !type) return [];
    const capabilitiesRaw = record(raw.capabilities);
    const reasoningRaw = record(capabilitiesRaw.reasoning);
    const allowedOptions = boundedStrings(reasoningRaw.allowed_options, 16);
    const defaultReasoning = boundedText(reasoningRaw.default, 64);
    const capabilities: NonNullable<HermesRuntimeModelDetail["capabilities"]> =
      {};
    if (typeof capabilitiesRaw.vision === "boolean")
      capabilities.vision = capabilitiesRaw.vision;
    if (typeof capabilitiesRaw.trained_for_tool_use === "boolean")
      capabilities.trainedForToolUse = capabilitiesRaw.trained_for_tool_use;
    if (allowedOptions.length)
      capabilities.reasoning = {
        allowedOptions,
        ...(allowedOptions.includes(defaultReasoning)
          ? { default: defaultReasoning }
          : {}),
      };
    const loadedRaw =
      Array.isArray(raw.loaded_instances) && raw.loaded_instances.length <= 16
        ? raw.loaded_instances
        : [];
    const loadedInstances = loadedRaw.flatMap(
      (instance): HermesRuntimeModelDetail["loadedInstances"] => {
        const row = record(instance);
        const instanceId = boundedText(row.id, 512);
        if (!instanceId) return [];
        const configRaw = record(row.config);
        const config: Record<string, boolean | number | string> = {};
        for (const [key, field] of Object.entries(configRaw).slice(0, 64)) {
          if (!/^[a-z][a-z0-9_]{0,63}$/.test(key)) continue;
          if (
            typeof field === "boolean" ||
            (typeof field === "string" && field.length <= 1_024) ||
            (typeof field === "number" && Number.isFinite(field))
          )
            config[key] = field;
        }
        return [{ id: instanceId, config }];
      },
    );
    const quantizationRaw = record(raw.quantization);
    const quantizationName = boundedText(quantizationRaw.name, 64);
    const bitsPerWeight = optionalNumber(
      quantizationRaw.bits_per_weight,
      0,
      64,
    );
    const detail: HermesRuntimeModelDetail = {
      id,
      type,
      loaded: raw.loaded === true,
      loadedInstances,
    };
    const texts = [
      ["display_name", "displayName", 256],
      ["publisher", "publisher", 128],
      ["architecture", "architecture", 128],
      ["format", "format", 64],
      ["parameters", "parameters", 64],
      ["selected_variant", "selectedVariant", 512],
    ] as const;
    for (const [source, target, maximum] of texts) {
      const text = boundedText(raw[source], maximum);
      if (text) Object.assign(detail, { [target]: text });
    }
    const maxContextLength = optionalNumber(
      raw.max_context_length,
      1,
      16_777_216,
    );
    const sizeBytes = optionalNumber(
      raw.size_bytes,
      0,
      Number.MAX_SAFE_INTEGER,
    );
    if (maxContextLength !== undefined)
      detail.maxContextLength = maxContextLength;
    if (sizeBytes !== undefined) detail.sizeBytes = sizeBytes;
    if (quantizationName || bitsPerWeight !== undefined)
      detail.quantization = {
        ...(quantizationName ? { name: quantizationName } : {}),
        ...(bitsPerWeight !== undefined ? { bitsPerWeight } : {}),
      };
    if (Object.keys(capabilities).length) detail.capabilities = capabilities;
    return [detail];
  });
}

export function normalizeRuntimeBaseUrl(value: unknown): string | null {
  const text = boundedText(value, 2_048);
  if (!text) return null;
  let parsed: URL;
  try {
    parsed = new URL(text);
  } catch {
    return null;
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return null;
  if (
    !parsed.hostname ||
    parsed.username ||
    parsed.password ||
    parsed.search ||
    parsed.hash
  )
    return null;
  return parsed.toString().replace(/\/$/, "");
}

export function assertRuntimeEndpointDraft(
  value: HermesRuntimeEndpointDraft,
): HermesRuntimeEndpointDraft {
  const id = value.id?.trim();
  const name = boundedText(value.name, 120);
  const baseUrl = normalizeRuntimeBaseUrl(value.baseUrl);
  const model = boundedText(value.model, 512);
  const contextLength = value.contextLength;
  if ((id && !identifierPattern.test(id)) || !name || !baseUrl || !model) {
    throw new Error("Enter a valid name, endpoint URL, and model ID.");
  }
  if (
    contextLength !== undefined &&
    (!Number.isSafeInteger(contextLength) ||
      contextLength < 1_024 ||
      contextLength > 16_777_216)
  ) {
    throw new Error(
      "Context length must be between 1,024 and 16,777,216 tokens.",
    );
  }
  return {
    ...(id ? { id } : {}),
    name,
    baseUrl,
    model,
    apiMode: normalizeApiMode(value.apiMode),
    discoverModels: value.discoverModels,
    makeDefault: value.makeDefault,
    ...(contextLength ? { contextLength } : {}),
    ...(value.models?.length ? { models: boundedStrings(value.models) } : {}),
  };
}

export function normalizeRuntimeEndpointSnapshot(
  value: unknown,
): HermesRuntimeEndpointSnapshot {
  const raw = record(value);
  const currentRaw = record(raw.current);
  const endpointsRaw =
    Array.isArray(raw.endpoints) && raw.endpoints.length <= 256
      ? raw.endpoints
      : [];
  const endpoints = endpointsRaw.flatMap(
    (candidate): HermesRuntimeEndpoint[] => {
      const endpoint = record(candidate);
      const id = boundedText(endpoint.id, 96);
      const name = boundedText(endpoint.name, 120);
      const baseUrl = normalizeRuntimeBaseUrl(endpoint.base_url);
      const model = boundedText(endpoint.model, 512);
      const source =
        endpoint.source === "direct-config" ? "direct-config" : "providers";
      const contextLength =
        typeof endpoint.context_length === "number" &&
        Number.isSafeInteger(endpoint.context_length) &&
        endpoint.context_length > 0
          ? endpoint.context_length
          : undefined;
      if (!identifierPattern.test(id) || !name || !baseUrl) return [];
      return [
        {
          id,
          name,
          baseUrl,
          model,
          apiMode: normalizeApiMode(endpoint.api_mode),
          models: boundedStrings(endpoint.models),
          discoverModels: endpoint.discover_models !== false,
          hasCredential: endpoint.has_api_key === true,
          isCurrent: endpoint.is_current === true,
          externalOverridesPresent:
            endpoint.external_overrides_present === true,
          profiles: normalizeProfiles(endpoint.profiles),
          source,
          ...(contextLength ? { contextLength } : {}),
        },
      ];
    },
  );
  return {
    current: {
      provider: boundedText(currentRaw.provider, 96),
      model: boundedText(currentRaw.model, 512),
      baseUrl: normalizeRuntimeBaseUrl(currentRaw.base_url) ?? "",
      apiMode: normalizeApiMode(currentRaw.api_mode),
    },
    endpoints,
  };
}

export function normalizeRuntimeEndpointValidation(
  value: unknown,
): HermesRuntimeEndpointValidation {
  const raw = record(value);
  return {
    ok: raw.ok === true,
    reachable: raw.reachable === true,
    message: boundedText(raw.message, 500),
    models: boundedStrings(raw.models),
    runtimeKind:
      raw.runtime_kind === "lm-studio"
        ? "lm-studio"
        : raw.runtime_kind === "openai-compatible"
          ? "openai-compatible"
          : "unknown",
    modelDetails: normalizeModelDetails(raw.model_details),
  };
}

export function runtimeProfilePayload(draft: HermesRuntimeProfileDraft) {
  if (
    !identifierPattern.test(draft.id.trim()) ||
    !boundedText(draft.name, 120) ||
    !boundedText(draft.model, 512)
  ) {
    throw new Error("Enter a valid profile ID, name, and exact model ID.");
  }
  const values: Record<string, unknown> = {};
  const mappings = [
    ["temperature", "temperature"],
    ["topP", "top_p"],
    ["frequencyPenalty", "frequency_penalty"],
    ["presencePenalty", "presence_penalty"],
    ["topK", "top_k"],
    ["minP", "min_p"],
    ["repeatPenalty", "repeat_penalty"],
    ["maxTokens", "max_tokens"],
    ["seed", "seed"],
    ["stop", "stop"],
  ] as const;
  for (const [source, target] of mappings) {
    const value = draft.overrides[source];
    if (value !== undefined) values[target] = value;
  }
  return {
    id: draft.id.trim(),
    name: draft.name.trim(),
    model: draft.model.trim(),
    overrides: values,
    make_active: draft.makeActive,
  };
}
