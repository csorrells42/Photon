# Reasoning / Thinking / Effort Control — Provider Compatibility Report

Date: 2026-08-11  
Owner: SPAT  
Status: source-complete and test-green; not published or mounted

## Outcome

The composer control is now a server-authoritative claim about one exact provider/model pair instead of a universal effort dropdown.

- Toggle-only models display `Thinking On/Off` or `Reasoning On/Off`.
- Effort-only models display only their real effort levels.
- Hybrid models display `Thinking On · Effort <level>` (or the provider's reasoning terminology) and a separate Off choice only when the transport has a proven Off payload.
- Mandatory-thinking models never offer Off.
- Unverified models display `Model default` and serialize no override.
- Docker Model Runner displays `Runner managed` and serializes no per-turn override.
- Model/session transitions invalidate stale values. Late loads and saves cannot restore the prior model's setting.
- The browser sends only a bounded option token. The backend validates that token against the descriptor cached for the exact live provider/model before changing the session.

The visible chip therefore does not claim `Ultra`, `Off`, or any other capability unless the selected provider/model contract proves it.

## Provider matrix

| Provider or route | Visible contract | Authoritative source | Outbound behavior |
|---|---|---|---|
| OpenAI API / OpenAI Codex | Model-specific effort or hybrid reasoning. GPT-5.6 exposes Off plus Low, Medium, High, Extra high, and Maximum; default Medium. | Versioned official compatibility table because OpenAI `/v1/models` does not publish reasoning capabilities. | Responses uses `reasoning.effort`; Chat Completions uses `reasoning_effort`. GPT-5.6 `none` is explicit, and the internal Ultra alias is clamped to the real `max` wire value. |
| Codex app-server runtime | The same bounded session selection. | Installed app-server `TurnStartParams.effort` protocol plus the selected model descriptor. | Each `turn/start` receives the current session effort. Unset or invalid values are omitted; explicit disabled becomes `none`. No stale replay after a model change. |
| Anthropic | Thinking and effort are represented independently. Modern mandatory/adaptive models show On plus only supported effort levels and no Off; supported legacy/manual models may expose Off. | Anthropic model capabilities plus exact-model compatibility rules. | Native `thinking` controls adaptive/manual/disabled behavior; `output_config.effort` carries effort where supported. Haiku 4.5 manual thinking is supported; older Haiku remains unsupported. |
| OpenRouter | Exact live options, default, mandatory state, and Off legality. | Per-model `reasoning` metadata from `GET /api/v1/models`. The coarse `supported_parameters: ["reasoning"]` flag is never treated as an option set. | Sends OpenRouter's nested `reasoning` payload. Mandatory models never receive `none`; incomplete metadata fails closed instead of falling through to model-name guesses. |
| DeepSeek V4 direct | `Thinking Off`, `Thinking On · Effort High`, and `Thinking On · Effort Maximum`; default On/High. | DeepSeek V4 official contract. | Off sends `thinking.type=disabled` and no effort. On sends `thinking.type=enabled` with `reasoning_effort=high|max`. Low/Medium are compatibility aliases for High and xHigh is an alias for Max; those aliases are not displayed as distinct behavior. |
| DeepSeek through OpenRouter or LM Studio | The gateway/runtime's exact published contract, not the direct-cloud model-name table. | OpenRouter live reasoning metadata or LM Studio live capability metadata. | Uses the selected route's bounded adapter. A `deepseek` substring cannot override trusted server metadata. |
| Gemini 2.5 Flash / Flash-Lite | Real Thinking Off/On toggle. | Google Gemini thinking contract. | Off uses `thinkingBudget: 0` (and the OpenAI-compatible snake-case equivalent), not merely `includeThoughts=false`. |
| Gemini 2.5 Pro | Thinking On only; no fake Off. | Google Gemini thinking contract. | A requested disabled value is omitted because this model cannot fully disable thinking. |
| Gemini 3 / 3.1 families | Real model-specific effort levels, with Thinking always On; no fake Off. | Google Gemini thinking contract. | Flash is clamped to Low/Medium/High; Pro is clamped to Low/High. Hiding returned thoughts is not represented as disabling model thinking. |
| LM Studio | Toggle, effort, hybrid, mandatory, or unsupported exactly as the active server publishes through `capabilities.reasoning.allowed_options/default`. | Live native LM Studio model metadata for the active endpoint. | Hermes bounds its existing LM Studio Chat Completions effort adapter to the advertised options and omits unset/invalid selections. See the transport limitation below. |
| Docker Model Runner | `Runner managed`; no per-message selector. | Docker's documented API has no per-request thinking/reasoning capability schema. | No reasoning field is sent. Persistent runner reasoning budgets remain a separate runtime-administration concern. |
| OpenCode Go GLM-5.2 | Thinking Off or On with High/Maximum effort. | Existing GLM-5.2 provider contract. | Off now sends `thinking.type=disabled`; enabled selections send only High or Max. |
| Nous | Proven effort/on choices only; no generic Off. | Existing Nous profile behavior rejects `reasoning.enabled=false`, and no alternate generic Off wire is proven. | Off is suppressed in the resolver instead of silently restoring the model default. |
| Unknown custom/gateway models | `Model default`. | None until the exact active endpoint proves a bounded contract. | No override is serialized. |

## State and UI behavior

- The server publishes label, exact options, aliases, default effort, default enabled state, mandatory state, toggle/effort support, provenance, and the bound provider/model identity.
- The React adapter preserves only bounded known tokens and treats missing/incomplete metadata as unverified.
- The selector renders one accessible button and a keyboard listbox with Arrow, Home, End, Enter, and Escape behavior plus focus restoration.
- Provider-supplied labels render as text.
- Model loading, switching, deferred switching, session opening, and saves disable the control.
- A present-but-empty session effort clears stale state; an absent field is treated as no update.
- New conversations reload the effective profile/model default instead of fabricating Medium.
- Model changes clear the prior session override and descriptor. Deferred changes remain locked until the backend confirms the active pair.
- Generation guards prevent late catalog/get/save responses from overwriting the current session/model state.

## Transport behavior covered

- Direct OpenAI/Codex Responses, OpenAI-compatible Chat Completions, and Codex app-server turns.
- Native Anthropic adaptive/manual thinking and effort.
- OpenRouter metadata, mandatory reasoning, and exact nested effort payloads.
- Direct DeepSeek V4 toggle plus canonical High/Max effort.
- LM Studio bounded option resolution and default omission.
- Gemini native and OpenAI-compatible thinking configuration.
- OpenCode Go GLM-5.2 Off/high/max.
- Generic fallback preservation of explicit disabled state.

## Validation evidence

All commands were run from the current shared source tree. No live provider request was made.

| Gate | Exact result |
|---|---|
| Focused DeepSeek backend | `37 passed` in `2.64s` |
| Combined reasoning-owned backend | `425 passed, 1 skipped` in `24.11s` |
| Reasoning-specific constants | `21 passed, 30 deselected` in `0.72s` |
| Focused React control/adapter/state | `3 files, 33 passed` in `267ms` |
| Widened AgentDock/Gateway/Settings | `21 files, 116 passed` in `4.29s` |
| Full frontend Vitest | `127 files, 781 passed` in `2.71s` |
| Strict TypeScript | `npm.cmd exec tsc -- -p tsconfig.app.json --noEmit` — pass in `6.9s` |
| Ruff | All listed Python implementation and test files passed in `0.7s` |
| Python compilation | All listed Python implementation files compiled with a disposable external `PYTHONPYCACHEPREFIX`; pass in `1.6s`; disposable cache removed |
| Nested-source diff check | `git diff --check` — pass |
| Outer reasoning-surface diff check | Scoped `git diff --check` — pass |

The widened backend command also demonstrated `463 passed, 11 skipped` before three unrelated environmental failures in `tests/test_hermes_constants.py`: one test assumes a different native Hermes home, and two require Windows symlink privilege. The exact reasoning-owned constants selection is green as recorded above.

The full outer repository `git diff --check` is not green because concurrent Photon CAD smoke files contain pre-existing trailing whitespace. The scoped reasoning surface and the entire nested source checkout are clean. This lane did not edit the CAD files.

## Limitations and integration notes

1. This is source/test evidence, not mounted runtime evidence. SPAT did not publish, relaunch, stop, or inspect the shared desktop process.
2. A selected-but-inactive custom endpoint is not probed using a renderer/catalog-provided URL. Activate that provider/model first; until the backend owns the endpoint, the selector remains unavailable. This avoids both stale attribution and arbitrary server-side probing.
3. LM Studio's native API documents the exact `reasoning` token on `/api/v1/chat`; its OpenAI Chat Completions documentation does not promise universal reasoning-control parity. Hermes retains its existing bounded Chat Completions adapter because that route has prior project evidence, but arbitrary future local model packages require mounted verification. The UI uses live LM Studio metadata and never invents a ladder when discovery fails.
4. Docker Model Runner exposes persistent runtime reasoning configuration, not a documented per-request control. This composer correctly leaves it runner-managed.
5. OpenAI and direct DeepSeek model-list APIs do not publish complete reasoning capabilities. Their compatibility tables must be updated when official model contracts change.
6. Existing ignored runtime configuration includes a separately recorded credential finding. No credential value was read into, returned by, or added to this control. This transaction never moved credentials into the browser and never included them in errors or descriptors.

## Official integration research

- OpenAI: [GPT-5.6 guidance](https://developers.openai.com/api/docs/guides/latest-model), [model catalog](https://developers.openai.com/api/docs/models), [Chat Completions request](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create), [Models API](https://developers.openai.com/api/reference/resources/models)
- Anthropic: [Thinking](https://platform.claude.com/docs/en/build-with-claude/thinking), [Effort](https://platform.claude.com/docs/en/build-with-claude/effort), [Extended thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking), [Models API](https://platform.claude.com/docs/en/api/models/list)
- OpenRouter: [Reasoning controls and discovery](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens), [Claude 4.6 mapping](https://openrouter.ai/docs/cookbook/evaluate-and-optimize/model-migrations/claude-4-6), [Claude 4.7 behavior](https://openrouter.ai/docs/cookbook/evaluate-and-optimize/model-migrations/claude-4-7)
- DeepSeek: [Thinking mode](https://api-docs.deepseek.com/guides/thinking_mode), [Chat Completion schema](https://api-docs.deepseek.com/api/create-chat-completion), [Models API](https://api-docs.deepseek.com/api/list-models), [Anthropic compatibility](https://api-docs.deepseek.com/guides/anthropic_api)
- Google Gemini: [Thinking](https://ai.google.dev/gemini-api/docs/generate-content/thinking)
- LM Studio: [native model capabilities](https://lmstudio.ai/docs/developer/rest/list), [native chat](https://lmstudio.ai/docs/developer/rest/chat), [Chat Completions](https://lmstudio.ai/docs/developer/openai-compat/chat-completions), [Responses](https://lmstudio.ai/docs/developer/openai-compat/responses)
- Docker: [Model Runner API](https://docs.docker.com/ai/model-runner/api-reference/), [runtime configuration](https://docs.docker.com/ai/model-runner/configuration/)

## Current frozen file inventory

Frontend/state surface:

- `src/Modules/HermesSettings/HermesModelAdapter.ts`
- `src/Modules/HermesSettings/HermesModelAdapter.test.ts`
- `src/Modules/AgentDock/HermesReasoningControl.tsx`
- `src/Modules/AgentDock/HermesReasoningControl.css`
- `src/Modules/AgentDock/HermesReasoningControl.test.tsx`
- `src/Modules/AgentDock/AgentDock.tsx` (shared mixed-origin file; preserve the complete frozen hash below)
- `src/Modules/HermesGateway/HermesReasoningState.ts`
- `src/Modules/HermesGateway/HermesReasoningState.test.ts`
- `src/Modules/HermesGateway/useHermesChat.ts`

Backend/provider surface:

- `source/hermes_cli/models.py`
- `source/hermes_cli/reasoning_controls.py`
- `source/hermes_constants.py`
- `source/tui_gateway/methods_config.py`
- `source/tui_gateway/methods_complete.py`
- `source/tui_gateway/server.py`
- `source/agent/transports/codex.py`
- `source/agent/transports/chat_completions.py`
- `source/agent/transports/codex_app_server_session.py`
- `source/agent/lmstudio_reasoning.py`
- `source/agent/anthropic_adapter.py`
- `source/agent/codex_runtime.py`
- `source/plugins/model-providers/openrouter/__init__.py`
- `source/plugins/model-providers/deepseek/__init__.py`
- `source/plugins/model-providers/opencode-zen/__init__.py`

Focused backend tests:

- `source/tests/tui_gateway/test_model_reasoning_options.py`
- `source/tests/tui_gateway/test_reasoning_session_scope.py`
- `source/tests/tui_gateway/test_make_agent_provider.py`
- `source/tests/hermes_cli/test_models.py`
- `source/tests/test_hermes_constants.py`
- `source/tests/agent/test_anthropic_adapter.py`
- `source/tests/agent/test_lmstudio_reasoning.py`
- `source/tests/agent/transports/test_chat_completions.py`
- `source/tests/agent/transports/test_codex_transport.py`
- `source/tests/agent/transports/test_codex_app_server_session.py`
- `source/tests/providers/test_provider_profiles.py`
- `source/tests/plugins/model_providers/test_deepseek_profile.py`
- `source/tests/plugins/model_providers/test_opencode_go_profile.py`
- `source/tests/run_agent/test_codex_app_server_integration.py`

## SHA-256 snapshot

```text
abf617827a447683f502552520a633026ad041d1d8a5ca8334839d075c6faf24  src/Modules/HermesSettings/HermesModelAdapter.ts
24ddb81398c005bd017154e2d7a92040cbd717e71bcf4f6cd34cded60cdbb37c  src/Modules/HermesSettings/HermesModelAdapter.test.ts
a1dd13eb33eeec5e22014533202e179f44a9fcabb58515814a731e2ae83329b8  src/Modules/AgentDock/HermesReasoningControl.tsx
c14816746dc379c299f2590ee664e2b59839c5fa50fc717f2d3da3e78965a08f  src/Modules/AgentDock/HermesReasoningControl.css
3d968fdc6780d0c132e66fca89ec94a4c64b8c280653b38bac5de2e9ae3ebc1c  src/Modules/AgentDock/HermesReasoningControl.test.tsx
602f45dbe6ccd13407e6b6dfeca615745df7be1dd69ddc8bb56cec867f5cc494  src/Modules/AgentDock/AgentDock.tsx
4776f88e486dc8afe54f6f3f750b37bd22d29f31ff787e6890c28f9e137c8d61  src/Modules/HermesGateway/HermesReasoningState.ts
60e5780f549382379b294d25d12e4e96ec798d364fcd2af0057337bc47ce6c47  src/Modules/HermesGateway/HermesReasoningState.test.ts
b982f1a79c21a06de366d2287dff157f97772e99055f777295a83031d2cc0a05  src/Modules/HermesGateway/useHermesChat.ts
47f467b181a5b4c467b35d0d6b145677ce750c73780c608a469b34a8e4e4640c  source/hermes_cli/models.py
2f2945c7a31cf19e46c571adc36ee993a5c2163fa47bf32feb506e61fa175520  source/hermes_cli/reasoning_controls.py
cb127df935291dec1e152b7794e4cd577cdaa1efadb6bcc77d243657555db778  source/hermes_constants.py
fc0f8daa098d9363438f889f5c9a005585e374d07f0cf478a1d1d8ada044c990  source/tui_gateway/methods_config.py
de8bfb9115a7840ebb1b27cc649292c2c43bcf1fe7ac9410fe1c99ae5cdc1d8d  source/tui_gateway/methods_complete.py
fdb71fd3132d116bdc9dc590b9d5d7547b44141f410201a2962a6ab8e64699f7  source/tui_gateway/server.py
804062a302cc638dbdceeb911cf9e2db0e4cc9001d40022295f354b8697bf0b6  source/agent/transports/codex.py
da93a5181b2fda13b3dfb272db1f27dd9f7acf18082175fd1a20382f2abd5b50  source/agent/transports/chat_completions.py
f57acf27c06c029cb41cc09ceec92264e1c4fed4ab121ccdb88ee7d0016acf40  source/agent/transports/codex_app_server_session.py
e5f386950631e65d850134703fb83911287860ed5fcf3d0fdae9416d120a09de  source/agent/lmstudio_reasoning.py
ef8be16b2eb02d71ec0e4c47bc38472a2cb243c904aa7eb6661348107b362271  source/agent/anthropic_adapter.py
a6d92336bde2048c23f6fe1b60920802338da048794f05536f7577a765b2e9cb  source/agent/codex_runtime.py
59ddde4e58ecf1c2b13ac120b759b24d63c4ebee7237fdc20c52223ce65691ce  source/plugins/model-providers/openrouter/__init__.py
ba539ccb926b054be69a1a5e552aaaee143aab90648751755ff717666125f69c  source/plugins/model-providers/deepseek/__init__.py
6e714fe833637f1f1a8a6e1a0f32685b48615ea9e73e406e7d38e8df27e2635a  source/plugins/model-providers/opencode-zen/__init__.py
ccb5188d407ba062e084ba0e1a8a8168d084050d895c978ebd2a33a59a948682  source/tests/tui_gateway/test_model_reasoning_options.py
91894af4ed4dad395d44c83b46fe3aabcda5d7cf48979ef10315d02fce8703aa  source/tests/tui_gateway/test_reasoning_session_scope.py
61f4774a8b51a790672ba156624e84e2fe8e51224439e4e4ebf809877b0d5e36  source/tests/tui_gateway/test_make_agent_provider.py
3d8036f11f16d07feb6b3bb54b5ae36fa65b9b6ea7438bea77449556db7b956b  source/tests/hermes_cli/test_models.py
6112088449551eaf5c863581eaf8fd3c9aec5c4a5946504164af9d8bb678983f  source/tests/test_hermes_constants.py
b0b6d93adbaace2606a7fc15fdc32c8253253499548f571525f02ba3627191bd  source/tests/agent/test_anthropic_adapter.py
117a4a6c67ad861b5727f7f11b5cbd07dc9f7969a1f290ae7e734e8e2341a38a  source/tests/agent/test_lmstudio_reasoning.py
3bdbfc24ba0e4daea19a0c51c0af947259f8621bb030a9495b4d8067427deaaa  source/tests/agent/transports/test_chat_completions.py
86f769a34986ecee9f30f9fb147e6557170dd3e22d47fe2436c8c86ec9f77db3  source/tests/agent/transports/test_codex_transport.py
cee1e737199cefd1bfa21c604cbdfad24fd72cdf7b894d5f5465d6af560580a4  source/tests/agent/transports/test_codex_app_server_session.py
b8c2132b281db1eb0dcaa000629335e228eee5ff589955936d0239dc6f76c5ff  source/tests/providers/test_provider_profiles.py
9786a9c14fa7fdf6fcce4caf5253b76062c3295677a6e72581380af137222c45  source/tests/plugins/model_providers/test_deepseek_profile.py
0c77a529c02ebc54c44b6ad6158da2db0628e0ebabada2ac787702654c1b7426  source/tests/plugins/model_providers/test_opencode_go_profile.py
ca6da632fde13d533112e6de4a97011a9dc4ee27e75088c5dd69cc6c622ac074  source/tests/run_agent/test_codex_app_server_integration.py
```

## Handoff

Architect should integrate/publish from the exact current-byte snapshot above, preserve mixed-origin shared files, and run the mounted provider/model acceptance pass. SPAT releases the reasoning/thinking/effort source-writer reservation after sending this report and its final report hash to Architect.
