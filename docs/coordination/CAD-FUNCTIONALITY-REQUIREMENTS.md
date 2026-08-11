# Photon CAD and assistant interoperability requirements

Status: active product requirements  
Priority: functionality first

## CAD completion

Photon CAD is complete only when the published desktop can create and reopen canonical projects; run Box, Cylinder, a real bearing, and a real Spur Gear; maintain assembly occurrences and transforms; show a sealed GLB preview; derive and retain BOM rows; run truthful verification; and export a generic STEP Part-21 copy. Source-only or fake smoke evidence is insufficient for the final claim.

### Manual design completion

Catalog parts and primitive generators do not, by themselves, complete manual CAD. The mounted desktop must also provide a usable direct-modeling sequence for selecting saved geometry and applying at least sketch/profile creation, extrude/add, cut, hole, fillet/chamfer, linear/circular pattern, rigid occurrence placement, and occurrence removal. Every accepted operation must update canonical STEP geometry, preview, occurrences, and BOM as one durable project transaction and must survive save, close, reopen, and refresh.

### External CAD file intake

External file intake is a high-value CAD requirement, with this order:

1. **P0: STEP/STP part import.** Select an existing generic STEP Part-21 file through an owned native picker; validate the complete bounded file; create a canonical Photon part with sealed source bytes, occurrence, BOM row, and rotatable GLB preview; save/reopen without returning to the source path.
2. **P0: STEP assembly import.** Preserve the actual imported product/occurrence hierarchy, names, part identities, and rigid transforms when the STEP payload contains assembly structure. Do not flatten a real assembly into one unnamed body merely to render it.
3. **P1: IPT/IAM conversion.** Advertise Autodesk Inventor part or assembly conversion only when a real, version-compatible Inventor/Apprentice or other independently verified converter is installed and receipt-bound. The converter must run in a separately owned process, emit generic STEP plus an exact hierarchy/transform manifest, and never claim native feature-history fidelity that was not preserved. On machines without that authority, IPT/IAM remains visibly unavailable rather than being treated as STEP.
4. **P2: additional 3D formats.** Add formats only through typed, discoverable converter capabilities with exact input/output/provenance claims. A file extension alone is never evidence that a format is supported.

Acceptance for each supported format requires a real representative file, exact native selection, bounded conversion, canonical save/reopen, rotatable preview, and a typed unsupported/cancelled/failure result that cannot be mistaken for success.

## Codex to Photon conversation bridge

### Current implementation status

The core bridge is already implemented and live. Do not replace or duplicate it.

- Host service: `src/Host/HermesDesktop/HermesConversationBridge.cs`
- Visible-dock adapter: `src/Modules/HermesGateway/HermesConversationBridgeAdapter.ts`
- Composer/transcript binding: `src/Modules/AgentDock/AgentDock.tsx`
- Local client: `src/Tools/HermesBridgeClient/`
- Settings: `%LOCALAPPDATA%\hermes\conversation-bridge.json`
- Loopback API: `GET /health`, authenticated `GET /v1/session`, `POST /v1/turns`, and `POST /v1/interrupt`

The current bridge submits through the visible dock's `send` action, waits for that turn to complete, and returns the bounded visible transcript and tool activity. Root verified the live endpoint and current Photon session on 2026-08-11. Therefore direct Codex-to-Photon conversation is available now through `hermes-bridge status`, `hermes-bridge send --text <message>`, and `hermes-bridge interrupt`.

Live acceptance on 2026-08-11 proved one visible send and one exact Photon reply with no duplicate. It also found a client/host timeout mismatch: the client defaults to 15 seconds, while the host supports a 30-minute Photon turn. The client can report timeout even though the visible turn later completes. The required correction is an acknowledged turn ID followed by a dedicated bounded wait/poll operation; health and status should retain short timeouts.

The requirements below distinguish the already-met core outcome from remaining product refinements. They must extend the existing bridge, not introduce a second authority.

### User outcome

Codex and Photon must be able to exchange messages directly through the same visible Photon chat conversation. When Photon encounters an error, Codex must be able to read the exact Photon response and reply without requiring the user to copy text, paste screenshots, or manually relay status.

### Functional requirements

1. **Met:** Codex can submit a text message to the currently selected Photon conversation.
2. **Met:** The submitted message appears once in the normal Photon transcript; there is no hidden second conversation.
3. **Met:** Codex can read bounded Photon assistant messages, tool activity, and error details from that same transcript.
4. **Remaining refinement:** add cursor-based incremental reads so reconnects cannot duplicate or skip committed items without returning the entire bounded snapshot.
5. **Partially met:** bridge transport requests have request IDs and typed HTTP outcomes; add a stable client-visible turn ID bound to the committed user/assistant messages.
6. **Partially met:** items carry session ID at snapshot level plus message ID, role, and bounded text; add committed timestamps and an explicit monotonically ordered cursor.
7. **Partially met:** `send` waits for the submitted turn and supports client cancellation; add a separately addressable wait/cancel-wait operation that never interrupts unrelated Photon work.
8. **Remaining refinement:** bind snapshots and pending waits to an explicit renderer/conversation generation and retire them on navigation or session replacement.
9. **Partially met:** sends are visible and `interrupt` exists; add a visible user-controlled relay-enabled state that rejects subsequent sends when disabled.
10. **Met:** the bridge uses authenticated loopback and the committed chat model without UI coordinates, clipboard automation, screenshot OCR, DOM scraping, or foreground-window dependence.

### Remaining implementation seam

- Extend the existing host-owned `HermesConversationBridge`; do not add a separate `PhotonConversationBridge` authority.
- Preserve the existing CLI and optionally expose a small local Codex-callable tool/MCP surface over the same loopback API:
  - `photon.send_message(conversationId, requestId, text)`
  - `photon.read_messages(conversationId, afterCursor, limit)`
  - `photon.wait_for_reply(conversationId, requestId, afterCursor, timeout)`
  - `photon.cancel_wait(requestId)`
- Route sends through the same state/action used by the visible composer. Route reads from the committed transcript/event model, not rendered pixels.
- Keep the renderer surface thin: it displays relay state and messages but does not own IPC, filesystem, process, or credential authority.
- The host binds each request to the exact desktop process, renderer generation, Photon session, and conversation.

### Acceptance

1. With Hermes visible, Codex sends `bridge test one`; it appears once in the Photon transcript.
2. Photon replies; Codex receives the exact reply once without user mediation.
3. Photon emits a deliberate bounded tool error; Codex receives its exact safe code/text and sends a corrective follow-up.
4. Restart/remount during a pending wait returns a typed stale-generation result and never attributes an old reply to the new session.
5. Ten send/reply cycles preserve order and produce no duplicates.
6. The user disables the relay; subsequent sends fail visibly and make no transcript change.

The core no-mediation outcome is available now. The bridge has since expanded into the four-peer Codex/Photon/Ali/Scarlett conversation bus defined in `docs/coordination/ASSISTANT-CONVERSATION-BUS-REQUIREMENTS.md`. That document is authoritative for sender/recipient identity, group routing, cursor/generation behavior, and loop prevention. The remaining refinements are a whole-Workbench functionality requirement, not a CAD-only feature, and should be closed before the final full-program functional audit is declared complete.
