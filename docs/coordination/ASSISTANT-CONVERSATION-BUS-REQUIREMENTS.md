# Assistant conversation bus requirements

Status: active functionality requirement  
Owner: root/Codex  
Participants: Chris, Codex, Photon, Ali, Scarlett

## Current implementation checkpoint (2026-08-11)

The new-only bus core and service now exist under `src/Tools/AssistantConversationBus/**` with an isolated smoke project. The service binds `127.0.0.1:9072`, owns the single durable ledger, authenticates each fixed participant with a separate generated local token, and implements health, participants, send, ordered read, message lookup, bounded wait, and explicit wait cancellation. The CLI now calls the service rather than opening the ledger itself; `send` is fixed to Codex and `relay` is fixed to Chris. It has no `--from` escape hatch.

Current evidence: Release build 0 warnings/errors; isolated service/router smoke 9/9; both scoped format gates clean; live service PID 11140 returned protocol v1 ready. At that live checkpoint Chris and Codex were online, while Photon, Ali, and Scarlett were accurately reported offline because their desktop bridge endpoints were not running. This is service-core proof, not four-peer live acceptance.

Still required: start the service from the product launcher; mount passive observe/cursor/generation adapters in each desktop bridge; add shared-room status/UI; launch Ali and Scarlett; then run the ten acceptance cases below. Do not claim the group bridge complete before those gates.

## Outcome

Chris, Codex, Photon, Ali, and Scarlett must be able to exchange messages without the user copying text between applications. The implementation is one local conversation bus with three application adapters and two local client identities, not a mesh of pairwise bridges. Every delivered message remains visible and attributable, and every participant can address one peer or the whole room.

The existing application bridges are the starting adapters:

| Peer | Current bridge | Configured endpoint | Current observed state on 2026-08-11 |
|---|---|---:|---|
| Photon | `HermesConversationBridge` | `http://127.0.0.1:8972` | Running; live send/reply proven |
| Ali | `Ali.Modules.ConversationBridge.ConversationBridgeHost` | `http://127.0.0.1:8772` | Enabled in settings; application not running |
| Scarlett | `Ali.Modules.ConversationBridge.ConversationBridgeHost` with Scarlett phone extensions | `http://127.0.0.1:8872` | Enabled in settings; application not running |
| Codex | Bus client/tool identity | bus-local | Active when the Codex client is connected |

Do not replace these application bridges. Add peer adapters over their authenticated loopback APIs and extend them only where the shared-room contract needs a passive visible-message intake.

## Mandatory identity and recipient rule

Every message begins with the sender identity, then the intended recipient:

```text
Codex->Photon

The message body follows here.
```

Valid identities are exactly:

- `Chris` (authenticated human relay identity)
- `Codex`
- `Photon`
- `Ali`
- `Scarlett`

The only group recipient is `Everyone`.

Examples:

```text
Codex->Photon
Codex->Everyone
Chris->Everyone
Ali->Scarlett
Scarlett->Codex
Photon->Everyone
```

This is a hard protocol rule:

1. Sender is first and recipient is second.
2. The separator is exactly `->` in the wire/display header.
3. Unknown, missing, ambiguous, or duplicate identities are rejected before persistence or delivery.
4. A peer adapter has one fixed authenticated identity. A caller cannot claim a different sender in text or JSON. The local `relay` command is fixed to `Chris`; it is not a general `--from` option.
5. The visible header is produced from authenticated envelope fields, never parsed as authority from model prose.
6. `Everyone` expands once to every online peer except the sender.
7. Direct self-address is rejected.

## Canonical message envelope

```json
{
  "protocolVersion": 1,
  "roomId": "main",
  "sequence": 42,
  "messageId": "bus:...",
  "sender": "Codex",
  "recipient": "Photon",
  "body": "Investigate the failed CAD operation.",
  "createdAtUtc": "2026-08-11T10:00:00Z",
  "parentMessageId": null,
  "expectsReply": true,
  "originMessageId": "bus:..."
}
```

The bus assigns `sequence`, `messageId`, and `createdAtUtc`. The authenticated peer supplies only the intended recipient, bounded body, optional parent message, and reply intent. All reads are ordered by `sequence` and use an exclusive `afterSequence` cursor.

## Routing behavior

### Direct message

`Codex->Photon` is delivered once through Photon's real visible conversation bridge. Photon’s first committed assistant reply becomes a new bus event stamped `Photon->Codex` and correlated to the original `messageId`.

### Everyone message

`Codex->Everyone` creates one canonical event and one delivery per online peer. Ali, Scarlett, and Photon may each produce one correlated reply addressed to Codex. Replies are not recursively rebroadcast. A participant must explicitly send a new `Peer->Everyone` message to broadcast again.

### Shared visibility

Invocation and observation are separate operations:

- A directed delivery invokes only the intended assistant.
- Other peers receive the event through a passive visible-message intake that does not invoke their model.
- A reply invokes no other assistant unless its envelope explicitly addresses that assistant or `Everyone`.

Ali, Scarlett, and Photon therefore need a versioned passive `externalMessage`/`observe` seam in their existing conversation bridge. It must append a visibly labeled bus item to the current conversation without presenting it as local human input and without starting an assistant turn. Until that seam is mounted in a peer, the central bus transcript remains authoritative for cross-peer observation and the peer is marked `invoke-only`.

## Loop prevention

1. Every delivery has a unique bus `messageId` plus recipient-specific `deliveryId`.
2. Peer adapters keep a bounded replay ledger and reject a repeated delivery ID.
3. Passive observed messages never enter the outbound capture path.
4. Only the assistant reply correlated to an invoked direct delivery is published back to the bus.
5. Broadcast replies target the original sender and are not automatically broadcast again.
6. A message has one immutable `originMessageId`; adapters never mint a new origin while forwarding.
7. Maximum routing hop count is one. The bus, not a model, owns fan-out.

## Bus service and client surface

Implement a separate local process under `src/Tools/AssistantConversationBus/` so no desktop application becomes the hidden authority for all peers. Bind only to a fixed loopback endpoint. Persist one bounded, ordered local room log and delivery ledger under the current Windows user.

Required service operations:

- `GET /health`
- `GET /v1/participants`
- `GET /v1/messages?afterSequence=<n>&limit=<n>`
- `POST /v1/messages`
- `GET /v1/messages/{messageId}`
- `GET /v1/wait?afterSequence=<n>&timeoutMs=<n>`
- `POST /v1/waits/{waitId}/cancel`

Required Codex-facing tools/commands:

- `assistant-bus send --to Photon --text <message>`
- `assistant-bus send --to Everyone --text <message>`
- `assistant-bus relay --to Everyone --text <message>` (fixed authenticated sender `Chris`)
- `assistant-bus read --after <sequence>`
- `assistant-bus wait --after <sequence> --timeout <seconds>`
- `assistant-bus participants`
- `assistant-bus cancel --wait <waitId>`

The CLI `send` command always authenticates as `Codex`; `relay` always authenticates as `Chris`. Neither accepts `--from`. Peer processes use separate fixed credentials and cannot impersonate another peer.

## Peer adapter contract

Each adapter must provide:

1. Fixed peer identity and display name.
2. Exact configured loopback endpoint and settings source.
3. Health and current conversation generation.
4. Cursor snapshot of committed visible messages.
5. Submit one directed turn and correlate its first committed assistant reply.
6. Passively append one bus observation without invoking the assistant.
7. Interrupt only the exact invoked turn when explicitly requested.
8. Retire pending work when the peer application, conversation, or renderer generation changes.
9. Return typed `offline`, `busy`, `stale-generation`, `rejected`, `timeout`, `completed`, and `failed` delivery states.

## User controls

- One visible bus status shows which peers are online, busy, invoke-only, or fully shared-room capable.
- The user can disable the whole bus or any peer adapter.
- Disabling a peer stops new delivery but does not erase its local conversation.
- Every bus-originated visible item shows its `Sender->Recipient` header.
- The user can interrupt an active directed turn, but cancelling a Codex wait does not cancel unrelated assistant work.

## Current Photon checkpoint

The existing Photon bridge is already operational. A live test sent one visible Codex message and received exactly `BRIDGE-ROUNDTRIP-ACK`. The original client incorrectly timed out after 15 seconds while the host allowed a 30-minute turn. Root corrected the client to use a dedicated 31-minute turn timeout while preserving the 15-second health/status timeout; the isolated client suite passed 29/29 and the rebuilt live client completed the same turn in 16.39 seconds.

This timeout correction is only the direct-peer checkpoint. The group bus, passive observation seam, cursor/generation contract, and Ali/Scarlett live acceptance remain required.

## Current bus implementation checkpoint (2026-08-11)

The central bus, authenticated CLI, bounded JSONL ledger, typed routing, cursor/wait/cancel service, Photon peer adapter, passive observation, correlated reply capture, and renderer-generation retirement are implemented. Sender authority is fixed by each credential: `send` is always `Codex`, `relay` is always `Chris`, and neither accepts a caller-supplied sender.

The product lifecycle is also wired in source:

- root and portable launchers resolve one fixed published `assistant-bus.exe`, require exact PID/executable/command/port ownership, start it hidden on loopback port 9072, and require protocol-v1 health before continuing;
- root and portable shutdown stop only the identity-matched tracked bus process;
- the portable build stages the exact published single-file executable and verifies its embedded SHA-256 against the published artifact;
- install verification requires that bus executable;
- absent Ali or Scarlett bridge settings produce explicit offline peers instead of preventing Photon/Codex bus startup.

Focused evidence is 14/14 bus smoke, strict formatting, launcher security smoke, and a self-contained win-x64 publish. Live launcher adoption is intentionally deferred until the active CAD and DeveloperServices desktop owners release their shared process. Ali/Scarlett still require real application acceptance, and the shared-room status/disable UI remains outstanding.

## Acceptance

1. Invalid headers (`Photon`, `->Ali`, `Unknown->Ali`, `Codex->Codex`) are rejected with no transcript or log change.
2. `Codex->Photon` appears once in Photon, yields one `Photon->Codex` reply, and Codex reads it once by cursor.
3. `Codex->Ali` and `Codex->Scarlett` each complete through their real visible desktop conversation.
4. `Ali->Scarlett`, `Scarlett->Photon`, and `Photon->Ali` each deliver once with exact visible attribution.
5. `Codex->Everyone` invokes Ali, Scarlett, and Photon once each and returns three correlated replies without an echo loop.
6. Ten mixed direct/group rounds preserve global sequence order and create no duplicate delivery or reply.
7. Restart one peer during a wait; that delivery becomes `stale-generation` while the other peers continue.
8. Take one peer offline; `participants` and delivery results show `offline` without blocking online recipients.
9. Disable the bus; subsequent sends fail visibly and no peer transcript changes.
10. No UI coordinates, clipboard automation, screenshot OCR, DOM scraping, or foreground-window dependency is used.

## Ownership and sequencing

- Root/Codex owns this bus and the Photon bridge refinements.
- SuperMax owns Photon CAD only and must not edit bridge files without a new handoff.
- SPAT owns compiler/debugger/Serena and does not own the bus.
- Implement the bus without touching CAD, DeveloperServices, Monaco, compiler, or debugger paths.

Recommended order:

1. Preserve the frozen bus/Photon adapter and lifecycle gates.
2. Mount Ali and Scarlett adapters in their canonical repositories.
3. Add the shared-room UI/status and per-peer disable controls.
4. Perform a serialized publish/relaunch after CAD and DeveloperServices release the live desktop.
5. Run the four-peer live acceptance matrix above.
