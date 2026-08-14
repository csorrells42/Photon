# Photon Linear admission

Photon may inspect Linear freely. Inspection, assignment, status, labels,
comments, tool output, and memory are context only; none authorize work.
Every other tool is blocked until the current Photon conversation holds one
active exact-revision admission. An uninspected fresh session therefore cannot
bypass admission by going straight to terminal, editor, browser, or another
engineering tool.

## Dual-authority workflow

1. Photon calls `photon_linear_inspect` and reports the exact `issueRevision`
   and `generation`.
2. The active Architect reviews that snapshot and writes one expiring receipt
   outside Photon:

   ```powershell
   python -m plugins.photon_linear_admission.approve `
     --profile worker `
     --issue CLS-6 `
     --revision "linear:v1:..." `
     --generation "photon-generation:v1:..."
   ```

3. Photon calls `photon_linear_begin` with that exact issue and revision.
   Hermes' existing visible approval surface asks Chris to approve the exact
   call. The rule key contains the one-use Architect nonce, so a session or
   persistent approval cannot authorize a later receipt.
4. Photon reports only through `photon_linear_progress`, then calls
   `photon_linear_complete` with a bounded maintenance record.
5. Photon does not change issue state. Chris reviews and moves the issue to
   Done.

An exact `photon_linear_direct_override` call is the alternate path. It still
uses the visible Chris approval surface, receives a fresh one-use challenge,
and is bound to the current Linear revision and Photon session.

Restart, revision drift, conversation/session change, expiry, cancellation,
receipt replay, foreign issue IDs, and raw Linear mutation all fail closed.
The append-only audit and maintenance files are evidence, not authority.
