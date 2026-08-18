import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ArrowDown,
  ArrowUp,
  Check,
  CircleAlert,
  CloudCog,
  ExternalLink,
  KeyRound,
  Layers3,
  LoaderCircle,
  Plus,
  RefreshCw,
  Save,
  ShieldCheck,
  Trash2,
  Workflow,
} from "lucide-react";
import {
  HERMES_DEFAULT_MODEL_CHANGED_EVENT,
  type HermesModelCatalog,
  type HermesModelSelection,
} from "../HermesSettings/HermesModelAdapter";
import {
  HermesRuntimeConfigurationWorkspace,
  SameOriginHermesRuntimeConfigurationClient,
} from "../HermesRuntimeConfiguration";
import {
  hermesModelManagementAdapter,
  type HermesAuxiliarySnapshot,
  type HermesCredentialPoolEntry,
  type HermesEnvironmentVariable,
  type HermesFallbackEntry,
  type HermesFallbackSnapshot,
  type HermesMoaConfig,
  type HermesMoaPreset,
  type HermesMoaSlot,
  type HermesModelDefaults,
  type HermesModelManagementAdapter,
  type HermesOAuthProvider,
  type HermesOAuthStart,
  type HermesProfileSnapshot,
} from "./HermesModelManagementAdapter";
import "./HermesModelManagementWorkspace.css";

type Section =
  "models" | "accounts" | "keys" | "pools" | "endpoints" | "routing" | "moa";
type Notice = { tone: "good" | "warn" | "bad"; text: string };
const reasoningEfforts = [
  "none",
  "enabled",
  "minimal",
  "low",
  "medium",
  "high",
  "xhigh",
  "max",
  "ultra",
] as const;

export function terminalCommandForExternalProvider(
  provider: HermesOAuthProvider,
) {
  const command = provider.cliCommand.trim();
  if (/^hermes auth add [a-z0-9][a-z0-9-]*$/i.test(command))
    return `docker exec -it hermes ${command}`;
  if (command === "copilot /login" || command === "claude setup-token")
    return command;
  return null;
}

const emptySelection = (): HermesModelSelection => ({
  provider: "",
  model: "",
});
const emptyMoaSlot = (): HermesMoaSlot => ({
  provider: "",
  model: "",
  enabled: true,
});
const emptyMoaPreset = (): HermesMoaPreset => ({
  reference_models: [emptyMoaSlot()],
  aggregator: emptyMoaSlot(),
  reference_temperature: null,
  aggregator_temperature: null,
  reference_timeout: null,
  degraded_reference_policy: "loud",
  max_tokens: 4096,
  reference_max_tokens: null,
  fanout: "user_turn",
  enabled: true,
});

function errorMessage(reason: unknown) {
  return reason instanceof Error
    ? reason.message
    : "Hermes could not complete that model-management request.";
}

function selectedProvider(
  catalog: HermesModelCatalog | null,
  selection: HermesModelSelection,
) {
  return catalog?.providers.find(
    (provider) => provider.slug === selection.provider,
  );
}

function defaultModelFor(catalog: HermesModelCatalog | null, provider: string) {
  const item = catalog?.providers.find(
    (candidate) => candidate.slug === provider,
  );
  if (!item) return "";
  if (
    provider === catalog?.currentProvider &&
    item.models.includes(catalog.currentModel)
  )
    return catalog.currentModel;
  return item.featuredModels[0] || item.models[0] || "";
}

function ModelFields({
  catalog,
  value,
  onChange,
  allowAuto = false,
  excludedProviders = [],
}: {
  catalog: HermesModelCatalog | null;
  value: HermesModelSelection;
  onChange: (value: HermesModelSelection) => void;
  allowAuto?: boolean;
  excludedProviders?: string[];
}) {
  const provider = selectedProvider(catalog, value);
  const listId = `models-${value.provider.replaceAll(/[^a-z0-9_-]/gi, "-") || "none"}`;
  return (
    <div className="hmm-model-fields">
      <label>
        <span>Provider</span>
        <select
          value={value.provider}
          onChange={(event) => {
            const nextProvider = event.target.value;
            onChange(
              nextProvider === "auto"
                ? { provider: "auto", model: "" }
                : {
                    provider: nextProvider,
                    model: defaultModelFor(catalog, nextProvider),
                  },
            );
          }}
        >
          {allowAuto ? (
            <option value="auto">auto — follow main model</option>
          ) : null}
          {catalog?.providers
            .filter((item) => !excludedProviders.includes(item.slug))
            .map((item) => (
              <option value={item.slug} key={item.slug}>
                {item.name}
                {item.authenticated ? "" : " · setup required"}
              </option>
            ))}
        </select>
      </label>
      <label>
        <span>Model</span>
        <input
          disabled={value.provider === "auto"}
          list={listId}
          value={value.model}
          onChange={(event) =>
            onChange({ ...value, model: event.target.value })
          }
          placeholder="Exact Hermes model ID"
        />
        <datalist id={listId}>
          {provider?.models.map((model) => (
            <option value={model} key={model} />
          ))}
        </datalist>
      </label>
    </div>
  );
}

export function HermesModelManagementWorkspace({
  adapter = hermesModelManagementAdapter,
  onRunExternalSetup,
  onOpenConnections,
}: {
  adapter?: HermesModelManagementAdapter;
  onRunExternalSetup?: (command: string) => void;
  onOpenConnections?: () => void;
}) {
  const [section, setSection] = useState<Section>("models");
  const [catalog, setCatalog] = useState<HermesModelCatalog | null>(null);
  const [mainDraft, setMainDraft] =
    useState<HermesModelSelection>(emptySelection);
  const [oauth, setOauth] = useState<HermesOAuthProvider[]>([]);
  const [environment, setEnvironment] = useState<HermesEnvironmentVariable[]>(
    [],
  );
  const [credentialPool, setCredentialPool] = useState<
    HermesCredentialPoolEntry[]
  >([]);
  const [auxiliary, setAuxiliary] = useState<HermesAuxiliarySnapshot | null>(
    null,
  );
  const [fallback, setFallback] = useState<HermesFallbackSnapshot | null>(null);
  const [moa, setMoa] = useState<HermesMoaConfig | null>(null);
  const [modelDefaults, setModelDefaults] =
    useState<HermesModelDefaults | null>(null);
  const [profiles, setProfiles] = useState<HermesProfileSnapshot | null>(null);
  const [selectedProfile, setSelectedProfile] = useState("");
  const [staleAuxiliary, setStaleAuxiliary] = useState<
    HermesAuxiliarySnapshot["tasks"]
  >([]);
  const [working, setWorking] = useState<string | null>("load");
  const [notice, setNotice] = useState<Notice | null>(null);
  const [keyEdits, setKeyEdits] = useState<Record<string, string>>({});
  const [customKey, setCustomKey] = useState({ name: "", value: "" });
  const [poolDraft, setPoolDraft] = useState({
    provider: "",
    label: "",
    apiKey: "",
  });
  const [oauthSession, setOauthSession] = useState<{
    provider: HermesOAuthProvider;
    session: HermesOAuthStart;
    code: string;
  } | null>(null);
  const [selectedMoaPreset, setSelectedMoaPreset] = useState("");
  const runtimeConfigurationClient = useMemo(
    () =>
      new SameOriginHermesRuntimeConfigurationClient(
        globalThis.fetch.bind(globalThis),
        selectedProfile,
      ),
    [selectedProfile],
  );
  const runtimeModelAdapter = useMemo(
    () => ({
      options: async (_sessionId?: string, refresh = false) =>
        adapter.modelCatalog(refresh),
      selectDefault: async (
        selection: HermesModelSelection,
        confirmExpensiveModel = false,
      ) => {
        const result = await adapter.setAssignment("main", selection, {
          confirmExpensiveModel,
        });
        return {
          confirmMessage: result.confirmMessage || undefined,
          confirmRequired: result.confirmRequired,
          deferred: false,
          model: selection.model,
        };
      },
    }),
    [adapter, selectedProfile],
  );

  const load = useCallback(
    async (refresh = false, signal?: AbortSignal) => {
      setWorking("load");
      setNotice(null);
      const results = await Promise.allSettled([
        adapter.modelCatalog(refresh),
        adapter.oauthProviders(),
        adapter.environment(),
        adapter.credentialPool(),
        adapter.auxiliary(),
        adapter.fallback(),
        adapter.moa(),
        adapter.modelDefaults(),
      ]);
      if (signal?.aborted) return;
      const [
        modelsResult,
        oauthResult,
        envResult,
        poolResult,
        auxiliaryResult,
        fallbackResult,
        moaResult,
        defaultsResult,
      ] = results;
      if (modelsResult.status === "fulfilled") {
        setCatalog(modelsResult.value);
        setMainDraft({
          provider: modelsResult.value.currentProvider,
          model: modelsResult.value.currentModel,
        });
      }
      if (oauthResult.status === "fulfilled") setOauth(oauthResult.value);
      if (envResult.status === "fulfilled") setEnvironment(envResult.value);
      if (poolResult.status === "fulfilled")
        setCredentialPool(poolResult.value);
      if (auxiliaryResult.status === "fulfilled") {
        setAuxiliary(auxiliaryResult.value);
        const mainProvider =
          modelsResult.status === "fulfilled"
            ? modelsResult.value.currentProvider.toLowerCase()
            : auxiliaryResult.value.main.provider.toLowerCase();
        setStaleAuxiliary(
          auxiliaryResult.value.tasks.filter((entry) => {
            const provider = entry.provider.toLowerCase();
            return Boolean(
              mainProvider &&
              provider &&
              provider !== "auto" &&
              provider !== mainProvider,
            );
          }),
        );
      }
      if (fallbackResult.status === "fulfilled")
        setFallback(fallbackResult.value);
      if (moaResult.status === "fulfilled") {
        setMoa(moaResult.value);
        setSelectedMoaPreset(
          moaResult.value.active_preset ||
            moaResult.value.default_preset ||
            Object.keys(moaResult.value.presets)[0] ||
            "",
        );
      }
      if (defaultsResult.status === "fulfilled")
        setModelDefaults(defaultsResult.value);
      const failures = results.filter(
        (result) => result.status === "rejected",
      ) as PromiseRejectedResult[];
      if (failures.length)
        setNotice({
          tone: "warn",
          text: `${results.length - failures.length} of ${results.length} Hermes model surfaces loaded. ${errorMessage(failures[0].reason)}`,
        });
      setWorking(null);
    },
    [adapter],
  );

  useEffect(() => {
    const abort = new AbortController();
    void adapter
      .profiles()
      .then(async (snapshot) => {
        if (abort.signal.aborted) return;
        const initialProfile =
          snapshot.current ||
          snapshot.active ||
          snapshot.profiles[0]?.name ||
          "default";
        setProfiles(snapshot);
        setSelectedProfile(initialProfile);
        adapter.selectProfile(initialProfile);
        await load(false, abort.signal);
      })
      .catch(async (reason) => {
        if (abort.signal.aborted) return;
        adapter.selectProfile("");
        setNotice({
          tone: "warn",
          text: `Profile discovery failed; managing the running profile. ${errorMessage(reason)}`,
        });
        await load(false, abort.signal);
      });
    return () => abort.abort();
  }, [adapter, load]);

  useEffect(() => {
    if (!oauthSession || oauthSession.session.flow !== "device_code") return;
    let disposed = false;
    const interval = window.setInterval(
      () => {
        void adapter
          .pollOAuth(oauthSession.provider.id, oauthSession.session.sessionId)
          .then(async (result) => {
            if (disposed || result.status === "pending") return;
            if (result.status === "approved") {
              setOauthSession(null);
              setNotice({
                tone: "good",
                text: `${oauthSession.provider.name} is connected to Hermes.`,
              });
              await load(true);
            } else {
              setOauthSession(null);
              setNotice({
                tone: "bad",
                text:
                  result.errorMessage ||
                  `${oauthSession.provider.name} authentication ${result.status}.`,
              });
            }
          })
          .catch((reason) => {
            if (!disposed)
              setNotice({ tone: "bad", text: errorMessage(reason) });
          });
      },
      Math.max(1_000, oauthSession.session.pollInterval * 1_000),
    );
    return () => {
      disposed = true;
      window.clearInterval(interval);
    };
  }, [adapter, load, oauthSession]);

  const providerVariables = useMemo(
    () => environment.filter((item) => item.category === "provider"),
    [environment],
  );
  const customVariables = useMemo(
    () => environment.filter((item) => item.category === "custom"),
    [environment],
  );
  const activePreset =
    moa && selectedMoaPreset ? moa.presets[selectedMoaPreset] : null;
  const appliedProvider = catalog?.providers.find(
    (provider) => provider.slug === catalog.currentProvider,
  );
  const appliedCapabilities =
    appliedProvider?.capabilities[catalog?.currentModel || ""];
  const reasoningDefaultSupported = appliedCapabilities?.reasoning ?? true;
  const fastDefaultSupported = appliedCapabilities?.fast ?? false;

  async function switchProfile(profile: string) {
    setSelectedProfile(profile);
    setOauthSession(null);
    setStaleAuxiliary([]);
    adapter.selectProfile(profile);
    await load(false);
  }

  async function saveMain(confirmExpensiveModel = false) {
    setWorking("main");
    setNotice(null);
    try {
      const result = await adapter.setAssignment("main", mainDraft, {
        confirmExpensiveModel,
      });
      if (result.confirmRequired) {
        if (
          window.confirm(
            result.confirmMessage ||
              "Hermes identified this as a higher-cost model. Use it as the global default?",
          )
        )
          await saveMain(true);
        return;
      }
      setStaleAuxiliary(result.staleAuxiliary);
      await load(true);
      window.dispatchEvent(new Event(HERMES_DEFAULT_MODEL_CHANGED_EVENT));
      setNotice({
        tone: "good",
        text: `${mainDraft.provider}/${mainDraft.model} is the Hermes default for new conversations.`,
      });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function saveDefaults(patch: Partial<HermesModelDefaults>) {
    if (!modelDefaults) return;
    const next = { ...modelDefaults, ...patch };
    setWorking("defaults");
    try {
      setModelDefaults(await adapter.saveModelDefaults(next));
      setNotice({
        tone: "good",
        text: "Hermes reasoning and speed defaults were saved for this profile.",
      });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function resetStaleAuxiliary() {
    setWorking("aux-reset");
    try {
      await adapter.setAssignment(
        "auxiliary",
        { provider: "auto", model: "" },
        { task: "__reset__" },
      );
      setAuxiliary(await adapter.auxiliary());
      setStaleAuxiliary([]);
      setNotice({
        tone: "good",
        text: "Every auxiliary slot now follows the main model.",
      });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function saveKey(item: HermesEnvironmentVariable) {
    const value = keyEdits[item.key] ?? "";
    if (!value) return;
    setWorking(`key:${item.key}`);
    try {
      await adapter.setEnvironment(item.key, value);
      setKeyEdits((current) => {
        const next = { ...current };
        delete next[item.key];
        return next;
      });
      await load(true);
      setNotice({
        tone: "good",
        text: `${item.key} was saved by Hermes and is now available to Photon.`,
      });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function clearKey(item: HermesEnvironmentVariable) {
    if (!window.confirm(`Clear ${item.key} from Hermes?`)) return;
    setWorking(`key:${item.key}`);
    try {
      await adapter.clearEnvironment(item.key);
      await load(true);
      setNotice({ tone: "good", text: `${item.key} was cleared.` });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function saveCustomKey() {
    const name = customKey.name.trim().toUpperCase();
    if (!/^[A-Z_][A-Z0-9_]*$/.test(name) || !customKey.value) {
      setNotice({
        tone: "bad",
        text: "Custom key names must be environment-variable names and a value is required.",
      });
      return;
    }
    setWorking("custom-key");
    try {
      await adapter.setEnvironment(name, customKey.value);
      setCustomKey({ name: "", value: "" });
      await load(true);
      setNotice({
        tone: "good",
        text: `${name} is available for custom endpoints and fallback routes.`,
      });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function addPoolEntry() {
    if (!poolDraft.provider || !poolDraft.apiKey) return;
    setWorking("pool-add");
    try {
      await adapter.addCredentialPoolEntry(
        poolDraft.provider,
        poolDraft.apiKey,
        poolDraft.label,
      );
      setPoolDraft({ provider: poolDraft.provider, label: "", apiKey: "" });
      setCredentialPool(await adapter.credentialPool());
      setNotice({
        tone: "good",
        text: `A rotation credential was added for ${poolDraft.provider}.`,
      });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function removePoolEntry(entry: HermesCredentialPoolEntry) {
    if (
      !window.confirm(
        `Remove ${entry.label || entry.id || `credential ${entry.index}`} from ${entry.provider}?`,
      )
    )
      return;
    setWorking(`pool:${entry.provider}:${entry.index}`);
    try {
      await adapter.removeCredentialPoolEntry(entry.provider, entry.index);
      setCredentialPool(await adapter.credentialPool());
      setNotice({
        tone: "good",
        text: `${entry.provider} credential removed.`,
      });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function beginOAuth(provider: HermesOAuthProvider) {
    if (provider.flow === "external") {
      const command = terminalCommandForExternalProvider(provider);
      if (!command) {
        setNotice({
          tone: "bad",
          text: `Hermes returned an unsupported external setup command for ${provider.name}. Photon refused to execute it.`,
        });
        return;
      }
      if (onRunExternalSetup) {
        onRunExternalSetup(command);
      } else {
        setNotice({
          tone: "warn",
          text: `${provider.name} is managed by its external CLI. Run in the Photon terminal: ${command}`,
        });
      }
      return;
    }
    setWorking(`oauth:${provider.id}`);
    try {
      const session = await adapter.startOAuth(provider.id);
      setOauthSession({ provider, session, code: "" });
      const url = session.authUrl || session.verificationUrl;
      if (url) window.open(url, "_blank", "noopener,noreferrer");
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function submitPkce() {
    if (!oauthSession?.code.trim()) return;
    setWorking(`oauth:${oauthSession.provider.id}`);
    try {
      await adapter.submitOAuthCode(
        oauthSession.provider.id,
        oauthSession.session.sessionId,
        oauthSession.code.trim(),
      );
      const name = oauthSession.provider.name;
      setOauthSession(null);
      await load(true);
      setNotice({ tone: "good", text: `${name} is connected to Hermes.` });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  async function disconnectOAuth(provider: HermesOAuthProvider) {
    if (!window.confirm(`Disconnect ${provider.name} from Hermes?`)) return;
    setWorking(`oauth:${provider.id}`);
    try {
      await adapter.disconnectOAuth(provider.id);
      await load(true);
      setNotice({ tone: "good", text: `${provider.name} was disconnected.` });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  function updateAuxiliary(task: string, selection: HermesModelSelection) {
    setAuxiliary((current) =>
      current
        ? {
            ...current,
            tasks: current.tasks.map((entry) =>
              entry.task === task
                ? entry.provider === selection.provider
                  ? { ...entry, ...selection }
                  : {
                      ...entry,
                      ...selection,
                      baseUrl: "",
                      apiMode: "",
                      keyEnv: "",
                      hasInlineApiKey: false,
                    }
                : entry,
            ),
          }
        : current,
    );
  }

  async function saveAuxiliary(
    entry: HermesAuxiliarySnapshot["tasks"][number],
  ) {
    setWorking(`aux:${entry.task}`);
    try {
      await adapter.setAssignment(
        "auxiliary",
        { provider: entry.provider, model: entry.model },
        {
          task: entry.task,
          baseUrl: entry.baseUrl,
          apiMode: entry.apiMode,
          keyEnv: entry.keyEnv,
        },
      );
      setAuxiliary(await adapter.auxiliary());
      setNotice({
        tone: "good",
        text: `${entry.task.replaceAll("_", " ")} now uses ${entry.provider === "auto" ? "the main model" : `${entry.provider}/${entry.model}`}.`,
      });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  function changeFallback(index: number, selection: HermesModelSelection) {
    setFallback((current) =>
      current
        ? {
            ...current,
            entries: current.entries.map((entry, position) =>
              position === index
                ? entry.provider === selection.provider
                  ? { ...entry, ...selection }
                  : { ...selection }
                : entry,
            ),
          }
        : current,
    );
  }

  function patchFallback(index: number, patch: Partial<HermesFallbackEntry>) {
    setFallback((current) =>
      current
        ? {
            ...current,
            entries: current.entries.map((entry, position) =>
              position === index ? { ...entry, ...patch } : entry,
            ),
          }
        : current,
    );
  }

  function moveFallback(index: number, direction: -1 | 1) {
    setFallback((current) => {
      if (!current) return current;
      const target = index + direction;
      if (target < 0 || target >= current.entries.length) return current;
      const entries = [...current.entries];
      [entries[index], entries[target]] = [entries[target], entries[index]];
      return { ...current, entries };
    });
  }

  async function saveFallback() {
    if (!fallback) return;
    setWorking("fallback");
    try {
      setFallback(await adapter.saveFallback(fallback.entries));
      setNotice({ tone: "good", text: "Hermes fallback order was saved." });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  function updatePreset(updater: (preset: HermesMoaPreset) => HermesMoaPreset) {
    if (!moa || !selectedMoaPreset || !activePreset) return;
    setMoa({
      ...moa,
      presets: { ...moa.presets, [selectedMoaPreset]: updater(activePreset) },
    });
  }

  function updateMoaSlot(
    kind: "aggregator" | "reference",
    index: number,
    selection: HermesModelSelection,
  ) {
    updatePreset((preset) =>
      kind === "aggregator"
        ? { ...preset, aggregator: { ...preset.aggregator, ...selection } }
        : {
            ...preset,
            reference_models: preset.reference_models.map((slot, position) =>
              position === index ? { ...slot, ...selection } : slot,
            ),
          },
    );
  }

  async function saveMoa() {
    if (!moa) return;
    for (const [name, preset] of Object.entries(moa.presets)) {
      const references = preset.reference_models.filter(
        (slot) => slot.enabled !== false,
      );
      if (
        !preset.aggregator.provider ||
        !preset.aggregator.model ||
        preset.aggregator.provider === "moa"
      ) {
        setNotice({
          tone: "bad",
          text: `MoA preset ${name} needs a non-MoA aggregator provider and model.`,
        });
        return;
      }
      if (
        !references.length ||
        references.some(
          (slot) => !slot.provider || !slot.model || slot.provider === "moa",
        )
      ) {
        setNotice({
          tone: "bad",
          text: `MoA preset ${name} needs at least one enabled, complete, non-MoA reference model.`,
        });
        return;
      }
    }
    setWorking("moa");
    try {
      setMoa(await adapter.saveMoa(moa));
      setNotice({
        tone: "good",
        text: "Hermes Mixture-of-Agents presets were saved.",
      });
    } catch (reason) {
      setNotice({ tone: "bad", text: errorMessage(reason) });
    } finally {
      setWorking(null);
    }
  }

  return (
    <main
      className="hermes-model-management"
      aria-label="Hermes model management"
    >
      <header className="hmm-header">
        <div>
          <small>PHOTON · HERMES MODEL SYSTEM</small>
          <h1>Models, subscriptions &amp; routing</h1>
          <p>
            One live control plane for every model backend the installed Hermes
            runtime advertises.
          </p>
        </div>
        <div className="hmm-header-actions">
          <label>
            <span>Profile</span>
            <select
              aria-label="Hermes profile"
              disabled={working === "load"}
              value={selectedProfile}
              onChange={(event) => void switchProfile(event.target.value)}
            >
              {profiles?.profiles.map((profile) => (
                <option value={profile.name} key={profile.name}>
                  {profile.name}
                  {profile.name === profiles.current
                    ? " · running"
                    : profile.name === profiles.active
                      ? " · active"
                      : ""}
                </option>
              ))}
            </select>
          </label>
          <button
            type="button"
            disabled={working === "load"}
            onClick={() => void load(true)}
          >
            <RefreshCw className={working === "load" ? "spin" : ""} size={14} />{" "}
            Refresh Hermes
          </button>
        </div>
      </header>
      <nav className="hmm-tabs" aria-label="Model management sections">
        {(
          [
            ["models", "Main model"],
            ["accounts", "Subscriptions"],
            ["keys", "API keys"],
            ["pools", "Rotation pools"],
            ["endpoints", "Local & custom"],
            ["routing", "Routing"],
            ["moa", "Mixture of Agents"],
          ] as const
        ).map(([id, label]) => (
          <button
            type="button"
            className={section === id ? "active" : ""}
            onClick={() => setSection(id)}
            key={id}
          >
            {label}
          </button>
        ))}
      </nav>
      {notice ? (
        <div className={`hmm-notice is-${notice.tone}`} role="status">
          {notice.tone === "good" ? (
            <Check size={14} />
          ) : (
            <CircleAlert size={14} />
          )}
          {notice.text}
        </div>
      ) : null}

      {section === "models" ? (
        <section className="hmm-section">
          <div className="hmm-truth">
            <span>
              <CloudCog size={20} />
            </span>
            <div>
              <small>HERMES GLOBAL DEFAULT</small>
              <strong>{catalog?.currentModel || "Not configured"}</strong>
              <p>
                {catalog?.currentProvider || "No provider"} · used by new
                conversations
              </p>
            </div>
          </div>
          <article className="hmm-card">
            <header>
              <div>
                <strong>Select the global main model</strong>
                <small>
                  {catalog?.providers.length ?? 0} provider variations reported
                  by Hermes
                </small>
              </div>
            </header>
            <ModelFields
              catalog={catalog}
              value={mainDraft}
              onChange={setMainDraft}
            />
            <div className="hmm-provider-state">
              {selectedProvider(catalog, mainDraft)?.authenticated ? (
                <>
                  <ShieldCheck size={13} /> Provider authenticated
                </>
              ) : (
                <>
                  <CircleAlert size={13} /> Provider setup is required in
                  Subscriptions or API keys
                </>
              )}
            </div>
            <button
              className="primary"
              type="button"
              disabled={
                !mainDraft.provider || !mainDraft.model || working !== null
              }
              onClick={() => void saveMain()}
            >
              <Save size={14} /> Use for new conversations
            </button>
          </article>
          <article className="hmm-card">
            <header>
              <div>
                <strong>Session truth</strong>
                <small>
                  Existing conversations retain their own Hermes model until
                  changed in the composer.
                </small>
              </div>
            </header>
            <p className="hmm-explanation">
              Photon now reads the global default and the active-session model
              independently. When they differ, the composer labels the model as
              a session override instead of pretending it is the default.
            </p>
          </article>
          {staleAuxiliary.length ? (
            <article className="hmm-card hmm-stale-warning">
              <header>
                <div>
                  <strong>
                    Auxiliary routes still use the previous provider
                  </strong>
                  <small>
                    {staleAuxiliary
                      .map((entry) => entry.task.replaceAll("_", " "))
                      .join(", ")}
                  </small>
                </div>
                <CircleAlert size={17} />
              </header>
              <p className="hmm-explanation">
                These explicit overrides do not automatically follow the new
                main model and may keep using a different subscription or API
                key.
              </p>
              <button
                type="button"
                disabled={working !== null}
                onClick={() => void resetStaleAuxiliary()}
              >
                Reset every auxiliary slot to main
              </button>
            </article>
          ) : null}
          {modelDefaults ? (
            <article className="hmm-card">
              <header>
                <div>
                  <strong>Defaults for new sessions</strong>
                  <small>
                    Profile {selectedProfile || profiles?.current || "current"}{" "}
                    · the composer can still override an existing session
                  </small>
                </div>
              </header>
              <div className="hmm-route-details">
                {reasoningDefaultSupported ? (
                  <label>
                    <span>Reasoning effort</span>
                    <select
                      value={modelDefaults.reasoningEffort}
                      disabled={working !== null}
                      onChange={(event) =>
                        void saveDefaults({
                          reasoningEffort: event.target.value,
                        })
                      }
                    >
                      {modelDefaults.reasoningOptions.map((effort) => (
                        <option value={effort} key={effort}>
                          {effort === "none" ? "Off" : effort}
                        </option>
                      ))}
                    </select>
                  </label>
                ) : null}
                {fastDefaultSupported ? (
                  <label className="hmm-check">
                    <span>Fast service tier</span>
                    <input
                      type="checkbox"
                      checked={modelDefaults.serviceTier === "fast"}
                      disabled={working !== null}
                      onChange={(event) =>
                        void saveDefaults({
                          serviceTier: event.target.checked ? "fast" : "normal",
                        })
                      }
                    />
                  </label>
                ) : null}
              </div>
              {!reasoningDefaultSupported && !fastDefaultSupported ? (
                <p className="hmm-explanation">
                  Hermes reports no global reasoning or fast-tier capability for
                  this model.
                </p>
              ) : null}
            </article>
          ) : null}
          <article className="hmm-card">
            <header>
              <div>
                <strong>
                  {catalog?.reasoningControl.label || "Reasoning"} variations
                </strong>
                <small>
                  {catalog?.reasoningControl.targetProvider ||
                    mainDraft.provider ||
                    "Hermes"}{" "}
                  /{" "}
                  {catalog?.reasoningControl.targetModel ||
                    mainDraft.model ||
                    "selected model"}{" "}
                  · {catalog?.reasoningControl.source || "unverified"}
                </small>
              </div>
            </header>
            <div className="hmm-reasoning-options">
              {catalog?.reasoningControl.options.length ? (
                catalog.reasoningControl.options.map((effort) => (
                  <span key={effort}>
                    {catalog.reasoningControl.optionLabels[effort] || effort}
                  </span>
                ))
              ) : (
                <p>
                  Hermes does not advertise a verified reasoning control for
                  this model. Photon leaves the control neutral instead of
                  inventing settings.
                </p>
              )}
            </div>
            <p className="hmm-explanation">
              The active conversation’s exact Hermes-supported control remains
              beside the composer, where changing it affects that session.
            </p>
          </article>
        </section>
      ) : null}

      {section === "accounts" ? (
        <section className="hmm-section">
          <div className="hmm-grid">
            {oauth.map((provider) => (
              <article className="hmm-card hmm-account" key={provider.id}>
                <header>
                  <span className={provider.status.loggedIn ? "connected" : ""}>
                    <KeyRound size={16} />
                  </span>
                  <div>
                    <strong>{provider.name}</strong>
                    <small>{provider.flow.replaceAll("_", " ")}</small>
                  </div>
                  <b>
                    {provider.status.loggedIn ? "Connected" : "Not connected"}
                  </b>
                </header>
                <p>
                  {provider.status.sourceLabel ||
                    provider.status.error ||
                    provider.disconnectHint ||
                    provider.cliCommand}
                </p>
                <footer>
                  {provider.docsUrl ? (
                    <a href={provider.docsUrl} target="_blank" rel="noreferrer">
                      Docs <ExternalLink size={11} />
                    </a>
                  ) : (
                    <span />
                  )}
                  {provider.status.loggedIn && provider.disconnectable ? (
                    <button
                      type="button"
                      disabled={working !== null}
                      onClick={() => void disconnectOAuth(provider)}
                    >
                      Disconnect
                    </button>
                  ) : provider.flow === "external" ? (
                    <button
                      className="primary"
                      type="button"
                      disabled={working !== null}
                      onClick={() => void beginOAuth(provider)}
                    >
                      {provider.status.loggedIn
                        ? "Manage in terminal"
                        : "Open setup in terminal"}
                    </button>
                  ) : provider.status.loggedIn ? (
                    <button type="button" onClick={() => setSection("keys")}>
                      Manage API key
                    </button>
                  ) : (
                    <button
                      className="primary"
                      type="button"
                      disabled={working !== null}
                      onClick={() => void beginOAuth(provider)}
                    >
                      Connect
                    </button>
                  )}
                </footer>
              </article>
            ))}
          </div>
          {oauthSession ? (
            <section className="hmm-auth-session">
              <header>
                <KeyRound size={17} />
                <div>
                  <strong>Connect {oauthSession.provider.name}</strong>
                  <small>
                    {oauthSession.session.flow.replaceAll("_", " ")}
                  </small>
                </div>
              </header>
              {oauthSession.session.userCode ? (
                <p>
                  Enter code <code>{oauthSession.session.userCode}</code> at{" "}
                  <a
                    href={oauthSession.session.verificationUrl}
                    target="_blank"
                    rel="noreferrer"
                  >
                    {oauthSession.session.verificationUrl}
                  </a>
                  . Photon is polling Hermes for completion.
                </p>
              ) : (
                <>
                  <p>
                    Complete the provider sign-in, then paste the returned
                    authorization code.
                  </p>
                  <input
                    value={oauthSession.code}
                    onChange={(event) =>
                      setOauthSession({
                        ...oauthSession,
                        code: event.target.value,
                      })
                    }
                    placeholder="Authorization code"
                  />
                </>
              )}
              <footer>
                <button
                  type="button"
                  onClick={() => {
                    void adapter.cancelOAuth(oauthSession.session.sessionId);
                    setOauthSession(null);
                  }}
                >
                  Cancel
                </button>
                {oauthSession.session.flow === "pkce" ? (
                  <button
                    className="primary"
                    type="button"
                    onClick={() => void submitPkce()}
                  >
                    Complete sign-in
                  </button>
                ) : null}
              </footer>
            </section>
          ) : null}
        </section>
      ) : null}

      {section === "keys" ? (
        <section className="hmm-section">
          <div className="hmm-section-heading">
            <div>
              <strong>Hermes provider credentials</strong>
              <small>
                {providerVariables.filter((item) => item.isSet).length} of{" "}
                {providerVariables.length} fields configured
              </small>
            </div>
          </div>
          <div className="hmm-key-list">
            {providerVariables.map((item) => (
              <article className="hmm-key" key={item.key}>
                <div>
                  <strong>
                    {item.providerLabel || item.provider || "Hermes provider"}
                  </strong>
                  <small>
                    {item.key} · {item.description || "Hermes provider setting"}
                    {item.advanced ? " · advanced" : ""}
                  </small>
                </div>
                <span className={item.isSet ? "set" : ""}>
                  {item.isSet ? item.redactedValue || "Set" : "Not set"}
                </span>
                <input
                  type={item.password ? "password" : "text"}
                  autoComplete="off"
                  value={keyEdits[item.key] ?? ""}
                  onChange={(event) =>
                    setKeyEdits((current) => ({
                      ...current,
                      [item.key]: event.target.value,
                    }))
                  }
                  placeholder={
                    item.isSet ? "Enter replacement value" : "Enter value"
                  }
                />
                <button
                  type="button"
                  disabled={!keyEdits[item.key] || working !== null}
                  onClick={() => void saveKey(item)}
                >
                  Save
                </button>
                {item.isSet ? (
                  <button
                    className="danger"
                    type="button"
                    disabled={working !== null}
                    onClick={() => void clearKey(item)}
                  >
                    <Trash2 size={12} />
                  </button>
                ) : null}
                {item.url ? (
                  <a
                    href={item.url}
                    target="_blank"
                    rel="noreferrer"
                    title="Provider instructions"
                  >
                    <ExternalLink size={12} />
                  </a>
                ) : null}
              </article>
            ))}
          </div>
          <article className="hmm-card">
            <header>
              <div>
                <strong>Custom endpoint keys</strong>
                <small>
                  Values stay in Hermes; routing fields contain only the
                  environment-variable name.
                </small>
              </div>
            </header>
            <div className="hmm-custom-key">
              <input
                value={customKey.name}
                onChange={(event) =>
                  setCustomKey((current) => ({
                    ...current,
                    name: event.target.value,
                  }))
                }
                placeholder="MY_MODEL_API_KEY"
              />
              <input
                type="password"
                autoComplete="off"
                value={customKey.value}
                onChange={(event) =>
                  setCustomKey((current) => ({
                    ...current,
                    value: event.target.value,
                  }))
                }
                placeholder="Secret value"
              />
              <button
                className="primary"
                type="button"
                disabled={
                  working !== null || !customKey.name.trim() || !customKey.value
                }
                onClick={() => void saveCustomKey()}
              >
                <Plus size={12} /> Add key
              </button>
            </div>
            {customVariables.length ? (
              <div className="hmm-custom-key-list">
                {customVariables.map((item) => (
                  <span key={item.key}>
                    <strong>{item.key}</strong>
                    <small>{item.redactedValue || "Set"}</small>
                    <button
                      className="danger"
                      type="button"
                      onClick={() => void clearKey(item)}
                    >
                      <Trash2 size={12} />
                    </button>
                  </span>
                ))}
              </div>
            ) : null}
          </article>
        </section>
      ) : null}

      {section === "pools" ? (
        <section className="hmm-section">
          <article className="hmm-card">
            <header>
              <div>
                <strong>Hermes credential rotation pools</strong>
                <small>
                  OAuth sign-ins appear here automatically; manual API keys can
                  be added to the same provider rotation.
                </small>
              </div>
            </header>
            <div className="hmm-pool-add">
              <select
                value={poolDraft.provider}
                onChange={(event) =>
                  setPoolDraft((current) => ({
                    ...current,
                    provider: event.target.value,
                  }))
                }
              >
                <option value="">Provider</option>
                {catalog?.providers
                  .filter((provider) => provider.slug !== "moa")
                  .map((provider) => (
                    <option value={provider.slug} key={provider.slug}>
                      {provider.name}
                    </option>
                  ))}
              </select>
              <input
                value={poolDraft.label}
                onChange={(event) =>
                  setPoolDraft((current) => ({
                    ...current,
                    label: event.target.value,
                  }))
                }
                placeholder="Label (optional)"
              />
              <input
                type="password"
                autoComplete="off"
                value={poolDraft.apiKey}
                onChange={(event) =>
                  setPoolDraft((current) => ({
                    ...current,
                    apiKey: event.target.value,
                  }))
                }
                placeholder="Additional API key"
              />
              <button
                className="primary"
                type="button"
                disabled={
                  working !== null || !poolDraft.provider || !poolDraft.apiKey
                }
                onClick={() => void addPoolEntry()}
              >
                <Plus size={12} /> Add
              </button>
            </div>
          </article>
          <div className="hmm-pool-list">
            {credentialPool.map((entry) => (
              <article
                className="hmm-card hmm-pool-entry"
                key={`${entry.provider}:${entry.index}:${entry.id}`}
              >
                <header>
                  <div>
                    <strong>
                      {entry.label || entry.id || `Credential ${entry.index}`}
                    </strong>
                    <small>
                      {entry.provider} · {entry.authType || "credential"} ·{" "}
                      {entry.source || "unknown source"}
                    </small>
                  </div>
                  <span>{entry.lastStatus || "ready"}</span>
                </header>
                <div>
                  <span>{entry.tokenPreview || "Token stored"}</span>
                  <span>{entry.requestCount.toLocaleString()} requests</span>
                  <span>
                    {entry.hasRefresh ? "Refreshable" : "Fixed credential"}
                  </span>
                </div>
                <button
                  className="danger"
                  type="button"
                  disabled={working !== null}
                  onClick={() => void removePoolEntry(entry)}
                >
                  <Trash2 size={12} /> Remove
                </button>
              </article>
            ))}
          </div>
          {credentialPool.length === 0 ? (
            <div className="hmm-empty">
              <Layers3 size={22} /> No pooled credentials are currently reported
              by Hermes.
            </div>
          ) : null}
        </section>
      ) : null}

      {section === "endpoints" ? (
        <section className="hmm-section hmm-endpoints">
          <HermesRuntimeConfigurationWorkspace
            client={runtimeConfigurationClient}
            modelAdapter={runtimeModelAdapter}
            onOpenConnections={() => onOpenConnections?.()}
          />
        </section>
      ) : null}

      {section === "routing" ? (
        <section className="hmm-section">
          <article className="hmm-card">
            <header>
              <div>
                <strong>Auxiliary model slots</strong>
                <small>
                  Each Hermes subsystem can follow the main model or use an
                  exact override.
                </small>
              </div>
            </header>
            <div className="hmm-routing-list">
              {auxiliary?.tasks.map((entry) => (
                <div className="hmm-route" key={entry.task}>
                  <strong>{entry.task.replaceAll("_", " ")}</strong>
                  <div className="hmm-route-body">
                    <ModelFields
                      catalog={catalog}
                      allowAuto
                      value={entry}
                      onChange={(selection) =>
                        updateAuxiliary(entry.task, selection)
                      }
                    />
                    <div className="hmm-route-details">
                      <label>
                        <span>Custom base URL</span>
                        <input
                          value={entry.baseUrl}
                          onChange={(event) =>
                            setAuxiliary((current) =>
                              current
                                ? {
                                    ...current,
                                    tasks: current.tasks.map((item) =>
                                      item.task === entry.task
                                        ? {
                                            ...item,
                                            baseUrl: event.target.value,
                                          }
                                        : item,
                                    ),
                                  }
                                : current,
                            )
                          }
                          placeholder="provider default"
                        />
                      </label>
                      <label>
                        <span>API mode</span>
                        <input
                          value={entry.apiMode}
                          onChange={(event) =>
                            setAuxiliary((current) =>
                              current
                                ? {
                                    ...current,
                                    tasks: current.tasks.map((item) =>
                                      item.task === entry.task
                                        ? {
                                            ...item,
                                            apiMode: event.target.value,
                                          }
                                        : item,
                                    ),
                                  }
                                : current,
                            )
                          }
                          placeholder="auto detect"
                        />
                      </label>
                      <label>
                        <span>Key environment variable</span>
                        <input
                          value={entry.keyEnv}
                          onChange={(event) =>
                            setAuxiliary((current) =>
                              current
                                ? {
                                    ...current,
                                    tasks: current.tasks.map((item) =>
                                      item.task === entry.task
                                        ? {
                                            ...item,
                                            keyEnv: event.target.value,
                                          }
                                        : item,
                                    ),
                                  }
                                : current,
                            )
                          }
                          placeholder={
                            entry.hasInlineApiKey
                              ? "Inline key preserved"
                              : "optional"
                          }
                        />
                      </label>
                    </div>
                  </div>
                  <button
                    type="button"
                    disabled={working !== null}
                    onClick={() => void saveAuxiliary(entry)}
                  >
                    Apply
                  </button>
                </div>
              ))}
            </div>
          </article>
          <article className="hmm-card">
            <header>
              <div>
                <strong>Ordered provider fallback chain</strong>
                <small>
                  Tried after the primary backend fails with authentication,
                  rate-limit, overload, or connection errors.
                </small>
              </div>
              <button
                type="button"
                onClick={() =>
                  setFallback((current) =>
                    current
                      ? {
                          ...current,
                          entries: [
                            ...current.entries,
                            {
                              provider: catalog?.providers[0]?.slug || "",
                              model: catalog?.providers[0]?.models[0] || "",
                            },
                          ],
                        }
                      : current,
                  )
                }
              >
                <Plus size={12} /> Add
              </button>
            </header>
            <div className="hmm-fallback-list">
              {fallback?.entries.map((entry, index) => (
                <div
                  className="hmm-fallback"
                  key={`${index}:${entry.provider}:${entry.model}`}
                >
                  <b>{index + 1}</b>
                  <div className="hmm-fallback-route">
                    <ModelFields
                      catalog={catalog}
                      value={entry}
                      onChange={(selection) => changeFallback(index, selection)}
                    />
                    <div className="hmm-route-details">
                      <label>
                        <span>Base URL</span>
                        <input
                          value={entry.baseUrl ?? ""}
                          onChange={(event) =>
                            patchFallback(index, {
                              baseUrl: event.target.value,
                            })
                          }
                          placeholder="provider default"
                        />
                      </label>
                      <label>
                        <span>API mode</span>
                        <input
                          value={entry.apiMode ?? ""}
                          onChange={(event) =>
                            patchFallback(index, {
                              apiMode: event.target.value,
                            })
                          }
                          placeholder="auto detect"
                        />
                      </label>
                      <label>
                        <span>Key environment variable</span>
                        <input
                          value={entry.keyEnv ?? ""}
                          onChange={(event) =>
                            patchFallback(index, { keyEnv: event.target.value })
                          }
                          placeholder={
                            entry.hasInlineApiKey
                              ? "Inline key preserved"
                              : "optional"
                          }
                        />
                      </label>
                    </div>
                  </div>
                  <div>
                    <button
                      type="button"
                      disabled={index === 0}
                      onClick={() => moveFallback(index, -1)}
                      title="Move up"
                    >
                      <ArrowUp size={12} />
                    </button>
                    <button
                      type="button"
                      disabled={index === fallback.entries.length - 1}
                      onClick={() => moveFallback(index, 1)}
                      title="Move down"
                    >
                      <ArrowDown size={12} />
                    </button>
                    <button
                      className="danger"
                      type="button"
                      onClick={() =>
                        setFallback({
                          ...fallback,
                          entries: fallback.entries.filter(
                            (_, position) => position !== index,
                          ),
                        })
                      }
                    >
                      <Trash2 size={12} />
                    </button>
                  </div>
                </div>
              ))}
            </div>
            <button
              className="primary"
              type="button"
              disabled={!fallback || working !== null}
              onClick={() => void saveFallback()}
            >
              <Save size={14} /> Save fallback order
            </button>
          </article>
        </section>
      ) : null}

      {section === "moa" ? (
        <section className="hmm-section">
          {moa ? (
            <>
              <article className="hmm-card">
                <header>
                  <span>
                    <Layers3 size={17} />
                  </span>
                  <div>
                    <strong>Mixture-of-Agents presets</strong>
                    <small>
                      Reference models advise; the aggregator produces the final
                      answer and calls tools.
                    </small>
                  </div>
                </header>
                <div className="hmm-preset-toolbar">
                  <label>
                    <span>Preset</span>
                    <select
                      value={selectedMoaPreset}
                      onChange={(event) =>
                        setSelectedMoaPreset(event.target.value)
                      }
                    >
                      {Object.keys(moa.presets).map((name) => (
                        <option value={name} key={name}>
                          {name}
                          {name === moa.active_preset ? " · active" : ""}
                          {name === moa.default_preset ? " · default" : ""}
                        </option>
                      ))}
                    </select>
                  </label>
                  <button
                    type="button"
                    onClick={() => {
                      const name = window.prompt("New MoA preset name")?.trim();
                      if (!name || moa.presets[name]) return;
                      setMoa({
                        ...moa,
                        presets: { ...moa.presets, [name]: emptyMoaPreset() },
                      });
                      setSelectedMoaPreset(name);
                    }}
                  >
                    <Plus size={12} /> New preset
                  </button>
                  <button
                    type="button"
                    disabled={
                      !selectedMoaPreset || Object.keys(moa.presets).length <= 1
                    }
                    onClick={() => {
                      const presets = { ...moa.presets };
                      delete presets[selectedMoaPreset];
                      const next = Object.keys(presets)[0];
                      setMoa({
                        ...moa,
                        presets,
                        active_preset:
                          moa.active_preset === selectedMoaPreset
                            ? next
                            : moa.active_preset,
                        default_preset:
                          moa.default_preset === selectedMoaPreset
                            ? next
                            : moa.default_preset,
                      });
                      setSelectedMoaPreset(next);
                    }}
                  >
                    <Trash2 size={12} /> Delete
                  </button>
                </div>
              </article>
              {activePreset ? (
                <article className="hmm-card">
                  <header>
                    <div>
                      <strong>{selectedMoaPreset}</strong>
                      <small>
                        {moa.active_preset === selectedMoaPreset
                          ? "Active Hermes preset"
                          : "Inactive preset"}
                        {moa.default_preset === selectedMoaPreset
                          ? " · default for MoA sessions"
                          : ""}
                      </small>
                    </div>
                    <label className="hmm-check">
                      <input
                        type="checkbox"
                        checked={activePreset.enabled}
                        onChange={(event) =>
                          updatePreset((preset) => ({
                            ...preset,
                            enabled: event.target.checked,
                          }))
                        }
                      />{" "}
                      Enabled
                    </label>
                  </header>
                  <section className="hmm-moa-block">
                    <h3>Reference models</h3>
                    {activePreset.reference_models.map((slot, index) => (
                      <div className="hmm-moa-slot" key={index}>
                        <ModelFields
                          catalog={catalog}
                          excludedProviders={["moa"]}
                          value={slot}
                          onChange={(selection) =>
                            updateMoaSlot("reference", index, selection)
                          }
                        />
                        <label className="hmm-check">
                          <span>Use reference</span>
                          <input
                            type="checkbox"
                            checked={slot.enabled !== false}
                            onChange={(event) =>
                              updatePreset((preset) => ({
                                ...preset,
                                reference_models: preset.reference_models.map(
                                  (item, position) =>
                                    position === index
                                      ? {
                                          ...item,
                                          enabled: event.target.checked,
                                        }
                                      : item,
                                ),
                              }))
                            }
                          />
                        </label>
                        <label>
                          <span>Reasoning effort</span>
                          <select
                            value={slot.reasoningEffort ?? ""}
                            onChange={(event) =>
                              updatePreset((preset) => ({
                                ...preset,
                                reference_models: preset.reference_models.map(
                                  (item, position) =>
                                    position === index
                                      ? {
                                          ...item,
                                          reasoningEffort:
                                            event.target.value || undefined,
                                        }
                                      : item,
                                ),
                              }))
                            }
                          >
                            <option value="">provider default</option>
                            {reasoningEfforts.map((effort) => (
                              <option value={effort} key={effort}>
                                {effort}
                              </option>
                            ))}
                          </select>
                        </label>
                        <label>
                          <span>Advisor max tokens</span>
                          <input
                            type="number"
                            min="1"
                            value={slot.maxTokens ?? ""}
                            onChange={(event) =>
                              updatePreset((preset) => ({
                                ...preset,
                                reference_models: preset.reference_models.map(
                                  (item, position) =>
                                    position === index
                                      ? {
                                          ...item,
                                          maxTokens:
                                            event.target.value === ""
                                              ? null
                                              : Number(event.target.value),
                                        }
                                      : item,
                                ),
                              }))
                            }
                            placeholder="preset default"
                          />
                        </label>
                        <button
                          className="danger"
                          type="button"
                          disabled={activePreset.reference_models.length <= 1}
                          onClick={() =>
                            updatePreset((preset) => ({
                              ...preset,
                              reference_models: preset.reference_models.filter(
                                (_, position) => position !== index,
                              ),
                            }))
                          }
                        >
                          <Trash2 size={12} />
                        </button>
                      </div>
                    ))}
                    <button
                      type="button"
                      onClick={() =>
                        updatePreset((preset) => ({
                          ...preset,
                          reference_models: [
                            ...preset.reference_models,
                            emptyMoaSlot(),
                          ],
                        }))
                      }
                    >
                      <Plus size={12} /> Add reference model
                    </button>
                  </section>
                  <section className="hmm-moa-block">
                    <h3>Aggregator</h3>
                    <div className="hmm-moa-slot">
                      <ModelFields
                        catalog={catalog}
                        excludedProviders={["moa"]}
                        value={activePreset.aggregator}
                        onChange={(selection) =>
                          updateMoaSlot("aggregator", 0, selection)
                        }
                      />
                      <label>
                        <span>Reasoning effort</span>
                        <select
                          value={activePreset.aggregator.reasoningEffort ?? ""}
                          onChange={(event) =>
                            updatePreset((preset) => ({
                              ...preset,
                              aggregator: {
                                ...preset.aggregator,
                                reasoningEffort:
                                  event.target.value || undefined,
                              },
                            }))
                          }
                        >
                          <option value="">provider default</option>
                          {reasoningEfforts.map((effort) => (
                            <option value={effort} key={effort}>
                              {effort}
                            </option>
                          ))}
                        </select>
                      </label>
                    </div>
                  </section>
                  <div className="hmm-moa-settings">
                    <label>
                      <span>Reference temperature</span>
                      <input
                        type="number"
                        step="0.1"
                        value={activePreset.reference_temperature ?? ""}
                        onChange={(event) =>
                          updatePreset((preset) => ({
                            ...preset,
                            reference_temperature:
                              event.target.value === ""
                                ? null
                                : Number(event.target.value),
                          }))
                        }
                      />
                    </label>
                    <label>
                      <span>Aggregator temperature</span>
                      <input
                        type="number"
                        step="0.1"
                        value={activePreset.aggregator_temperature ?? ""}
                        onChange={(event) =>
                          updatePreset((preset) => ({
                            ...preset,
                            aggregator_temperature:
                              event.target.value === ""
                                ? null
                                : Number(event.target.value),
                          }))
                        }
                      />
                    </label>
                    <label>
                      <span>Reference timeout seconds</span>
                      <input
                        type="number"
                        min="1"
                        value={activePreset.reference_timeout ?? ""}
                        onChange={(event) =>
                          updatePreset((preset) => ({
                            ...preset,
                            reference_timeout:
                              event.target.value === ""
                                ? null
                                : Number(event.target.value),
                          }))
                        }
                      />
                    </label>
                    <label>
                      <span>Reference max tokens</span>
                      <input
                        type="number"
                        min="1"
                        value={activePreset.reference_max_tokens ?? ""}
                        onChange={(event) =>
                          updatePreset((preset) => ({
                            ...preset,
                            reference_max_tokens:
                              event.target.value === ""
                                ? null
                                : Number(event.target.value),
                          }))
                        }
                      />
                    </label>
                    <label>
                      <span>Aggregator max tokens</span>
                      <input
                        type="number"
                        min="1"
                        value={activePreset.max_tokens}
                        onChange={(event) =>
                          updatePreset((preset) => ({
                            ...preset,
                            max_tokens: Number(event.target.value),
                          }))
                        }
                      />
                    </label>
                    <label>
                      <span>Fan-out cadence</span>
                      <input
                        value={activePreset.fanout ?? ""}
                        onChange={(event) =>
                          updatePreset((preset) => ({
                            ...preset,
                            fanout: event.target.value,
                          }))
                        }
                        placeholder="user_turn"
                      />
                    </label>
                    <label>
                      <span>Degraded reference policy</span>
                      <select
                        value={activePreset.degraded_reference_policy}
                        onChange={(event) =>
                          updatePreset((preset) => ({
                            ...preset,
                            degraded_reference_policy:
                              event.target.value === "silent"
                                ? "silent"
                                : "loud",
                          }))
                        }
                      >
                        <option value="loud">Loud</option>
                        <option value="silent">Silent</option>
                      </select>
                    </label>
                  </div>
                  <div className="hmm-moa-settings">
                    <label>
                      <span>Advisor privacy filter</span>
                      <select
                        value={moa.privacyFilter}
                        onChange={(event) =>
                          setMoa({
                            ...moa,
                            privacyFilter:
                              event.target.value === "display" ||
                              event.target.value === "full"
                                ? event.target.value
                                : "",
                          })
                        }
                      >
                        <option value="">Off</option>
                        <option value="display">Display and traces</option>
                        <option value="full">Full aggregator protection</option>
                      </select>
                    </label>
                    <label className="hmm-check">
                      <span>Save full MoA traces</span>
                      <input
                        type="checkbox"
                        checked={moa.saveTraces}
                        onChange={(event) =>
                          setMoa({ ...moa, saveTraces: event.target.checked })
                        }
                      />
                    </label>
                    <label>
                      <span>Trace directory</span>
                      <input
                        disabled={!moa.saveTraces}
                        value={moa.traceDir}
                        onChange={(event) =>
                          setMoa({ ...moa, traceDir: event.target.value })
                        }
                        placeholder="Hermes default trace directory"
                      />
                    </label>
                  </div>
                  <p className="hmm-explanation">
                    Trace paths are resolved by the Hermes runtime. In Docker,
                    use a path inside its persistent data volume.
                  </p>
                  <footer className="hmm-card-actions">
                    <button
                      type="button"
                      disabled={moa.active_preset === selectedMoaPreset}
                      onClick={() =>
                        setMoa({ ...moa, active_preset: selectedMoaPreset })
                      }
                    >
                      <Workflow size={13} /> Make active
                    </button>
                    <button
                      type="button"
                      disabled={moa.default_preset === selectedMoaPreset}
                      onClick={() =>
                        setMoa({ ...moa, default_preset: selectedMoaPreset })
                      }
                    >
                      <Check size={13} /> Make default
                    </button>
                    <button
                      className="primary"
                      type="button"
                      disabled={working !== null}
                      onClick={() => void saveMoa()}
                    >
                      <Save size={14} /> Save all presets
                    </button>
                  </footer>
                </article>
              ) : null}
            </>
          ) : (
            <div className="hmm-empty">
              <LoaderCircle className={working ? "spin" : ""} size={22} />{" "}
              Hermes did not return a Mixture-of-Agents configuration.
            </div>
          )}
        </section>
      ) : null}
    </main>
  );
}
