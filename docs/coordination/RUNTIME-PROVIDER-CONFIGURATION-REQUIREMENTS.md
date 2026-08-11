# Runtime provider configuration requirements

Date: 2026-08-11 (America/Chicago)

## Objective

Provide one truthful Runtime workspace for hosted providers, LM Studio, and other OpenAI-compatible servers. Preserve the depth of LM Studio's model configuration while preventing Photon and the server from presenting contradictory controls.

## Non-negotiable truth rules

1. An unset Photon profile field is omitted from the inference request. The server or model default remains authoritative.
2. Every displayed value carries one provenance state:
   - `server-reported`
   - `photon-profile-override`
   - `server-managed-unreported`
   - `unsupported`
3. Photon never infers an unreported runtime value and displays it as fact.
4. Unknown is a valid state. UI text must say that LM Studio manages a value when its API does not expose it.
5. No hidden policy silently rewrites a visible generation control. A mandatory application boundary must be named, visible before send, and must reject an incompatible request rather than mutate it invisibly.
6. Effective configuration is computed as `server/default value + explicit Photon profile fields only`. The UI shows the source of each effective field.
7. Reasoning effort is conversation-scoped unless the user deliberately stores it in a named profile. Model/server capability metadata bounds the available options.
8. Secrets remain in the native credential boundary. Profiles store references, never secret values.

## LM Studio discovery

Photon should use LM Studio's native v1 model inventory when available and fall back to the OpenAI-compatible model list only when necessary.

The native inventory can truthfully supply:

- model key and display name;
- LLM versus embedding type;
- architecture, format, quantization, parameter count and size;
- maximum supported context length;
- loaded instances and their exact reported load configuration;
- vision, tool-use and reasoning capability metadata;
- server-published reasoning choices and default.

Any generation preset value that LM Studio does not publish remains `server-managed-unreported`. Photon must not read private LM Studio files or reverse-engineer GUI state to invent parity.

## Profiles

Profiles are named and scoped by exact normalized server identity plus model key. A profile contains only fields the user explicitly elected to override.

Required operations:

- create, rename, duplicate and delete a profile;
- select `Server defaults` or one named profile per server/model;
- show a before-send effective configuration and field provenance;
- apply the selected profile to new conversations without retroactively mutating active conversations;
- retain distinct profiles for the same model served by different endpoints;
- reject stale profiles when the server identity or model capability contract changes incompatibly.

## Initial controls

The first complete profile schema may cover generation and load controls that the target server explicitly supports, including reasoning effort, temperature, top-p, top-k, min-p, repeat/frequency/presence penalties, seed, maximum output tokens, stop sequences, context length, batch sizes, flash attention, expert count and KV-cache placement.

Every field is tri-state: inherited, explicitly overridden, or unsupported. Inherited is the default.

## Model lifecycle

Runtime must display loaded versus downloaded state. Explicit load/unload actions use server-owned APIs and report the resulting model-instance identity. Photon must not claim a model was unloaded merely because the selected model changed.

For LM Studio, ordinary model selection should continue to respect LM Studio JIT loading and auto-eviction settings. Manual model instances are not silently evicted by Photon.

## Docker Model Runner evaluation

Docker Model Runner is an additional provider candidate, not an automatic replacement for LM Studio. GPT-OSS migration requires a measured comparison of:

- model and capability discovery;
- explicit load/unload and memory behavior;
- generation and load-control depth;
- reasoning-effort support;
- throughput, first-token latency and sustained latency;
- tool-call correctness and structured-output reliability;
- effective-setting provenance and reproducibility.

LM Studio remains the preferred runtime while Docker Model Runner cannot provide an equally truthful and controllable experience.

## Acceptance

1. Connect to the live LM Studio native API and list the exact current models without fabricated metadata.
2. Select `Server defaults`; inspect the outbound request and prove no generation/load fields are added other than required protocol fields.
3. Save two differently named profiles for the same model and prove only selected explicit fields differ.
4. Prove a second endpoint with the same model key does not share profiles.
5. Prove unsupported and unreported settings never appear as effective numeric or boolean values.
6. Prove reasoning choices come from server capability metadata when published and use a clearly labeled compatibility fallback otherwise.
7. Prove deleting or deselecting a profile restores omission-based server defaults.
8. Prove no credential, endpoint secret or private server filesystem path enters renderer state or profile persistence.
