# Photon ↔ Linear admission handoff

Date: 2026-08-14  
State: mounted and functional through read-only inspection; exact CLS-6 dual-authority approval is currently waiting for Chris  
Next owner: Architect/root after task archival

## Product rule

Photon may inspect Linear context, but memory, assignment, issue state, labels, comments, and Photon inference never authorize engineering work. Execution requires either:

1. an expiring Architect receipt plus a visible Chris approval for the same issue ID, exact issue revision, Photon generation, and conversation; or
2. an explicit visible Chris direct override.

Photon cannot raw-mutate Linear or close issues. Completion records bounded maintenance evidence and leaves the issue for Chris to review/close.

## Current mounted state

- Linear OAuth is complete and persisted under the mounted Hermes data directory. Do not print or copy token contents.
- `docker exec hermes hermes mcp test linear` passes: connected and 53 tools discovered.
- The configured Photon Linear toolset exposes only the bounded subset needed by the product flow.
- Adopted immutable runtime generation: `6895e6251d822dbf-2e0d19062ab7`.
- Adopted image: `sha256:2e0d19062ab7ce6366c6ec34209e4ab377371c14e09c42af1f84b17dd8df4970`.
- Previous image: `sha256:843bd01483fef613a761815364aced8a339c2725989bba7928b04b64d3c8065e`.
- Live Photon conversation session: `20260814_120442_cd38e9`.
- Renderer generation: `renderer:e6bcb76d556f17736b62e26aa5e5c8fc`.

## Mounted evidence

Before admission, a harmless engineering tool was rejected exactly:

`Photon has no active exact-revision admission; engineering tools are blocked.`

After the final MCP-envelope correction, mounted `photon_linear_inspect` succeeded for CLS-6 and returned:

- issue: `CLS-6`
- issue revision: `linear:v1:2026-08-14T09:48:09.462Z:11dd4a18c3d642a8bde61e5f4b5c503e35ec4b5972e61321d1618ef7a3fbd4fe`
- Photon generation: `photon-generation:v1:a76223ddd79830887ef9d672cb376bd79224b6d4c46d0aa8a55993bbf48474e2`
- authority: `context-only`
- execution authorized: `false`
- Linear status: `In Progress`

The Architect receipt for that exact binding was written out-of-band and originally expires at `2026-08-14 08:01:03 -05:00`. It is single-use and intentionally short-lived.

Photon has described `photon_linear_begin` and is waiting at the visible Chris approval step. At handoff time the conversation reports `isBusy: true`; last visible message is:

`Schema confirmed. Making the single photon_linear_begin admission call with the exact inspected revision.`

## Immediate continuation

1. In the visible Photon approval card, Chris approves the exact CLS-6 revision above.
2. If the approval card or Architect receipt expired, do not loosen the gate. Re-run one `photon_linear_inspect`, use the newly returned exact revision/generation, and write a fresh receipt:

   ```powershell
   docker exec hermes python -m plugins.photon_linear_admission.approve `
     --profile default `
     --issue CLS-6 `
     --revision '<exact revision returned by inspect>' `
     --generation '<exact generation returned by inspect>'
   ```

3. Ask Photon to call `photon_linear_begin` once with the exact issue ID/revision and wait for visible Chris approval.
4. Read back the returned `admissionId`; prove one harmless engineering read succeeds only under that admission.
5. Prove foreign issue, stale revision, raw Linear mutation, and issue closure remain blocked.
6. Use `photon_linear_progress` for bounded progress and `photon_linear_complete` for the structured maintenance record. Completion must leave Linear state unchanged and return `ready-for-chris-review`.
7. Architect/root may then post the final independent evidence comment and move CLS-6 to **In Review**, never Done. Chris closes it after review.

## Source correction and evidence

The final defect was not argument loss. Hermes MCP tools return text payloads as an envelope shaped like `{"result":"<JSON string>"}`. The admission parser decoded only the outer mapping, then looked for an issue identifier on the wrapper. The final correction recursively unwraps bounded `result`/`structuredContent` envelopes and still fails closed on malformed or error payloads.

Focused gate:

`source/.venv/Scripts/python.exe -m pytest source/tests/plugins/test_photon_linear_admission.py -q`

Result: `22 passed in 0.64s`.

Current exact SHA-256:

- `E74365B234C24C11F7EF0A578442CF5B7747D50769D9B4332DB5D2CA440BA44B` — `source/plugins/photon_linear_admission/__init__.py`
- `D81C7286DC88659C39597F6DCA9889AEBA7748F9269B0FF03ECE2E5664FB01A6` — `source/plugins/photon_linear_admission/authority.py`
- `41EDB6657BB01AA81398254B21373626EF1E0F06F5B30F672B512471984E5F6B` — `source/tests/plugins/test_photon_linear_admission.py`

Runtime build evidence:

- source digest: `6895e6251d822dbf4003bfec203f0a575e2514d268b7d397ca7eca99bc049aaa`
- build request: `logs/runtime-builds/6895e6251d822dbf4003bfec203f0a575e2514d268b7d397ca7eca99bc049aaa/runtime-build-request.json`
- committed generation receipt: `logs/runtime-generations/runtime-generation.current.json`

## Operational notes

- Use the mounted conversation client at `src/Tools/HermesBridgeClient/bin/Release/net10.0/hermes-bridge.exe` for status/send operations.
- The bridge `send` command can outlive its local five-second client wait; check `status` rather than resending.
- Do not retry a failed admission blindly. Re-inspect and compare exact revision/generation first.
- Do not reveal OAuth token files or credential values in logs, tickets, maintenance memory, or handoffs.
- `hermes mcp test linear` currently emits a Python async-close `RuntimeWarning` after the successful 53-tool discovery. It did not affect authentication or tool registration, but remains a cleanup-quality issue rather than acceptance evidence.
- Earlier intermediate images were superseded. Only generation `6895e6251d822dbf-2e0d19062ab7` is authoritative for this handoff.

