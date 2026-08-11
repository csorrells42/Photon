# Hermes conversation bridge

Hermes Workbench exposes an authenticated, loopback-only conversation bridge so trusted local tools can inspect and test the same Hermes dock the user sees.

## Stable endpoint

- Ali: `http://127.0.0.1:8772`
- Scarlett: `http://127.0.0.1:8872`
- Hermes: `http://127.0.0.1:8972`

The Hermes host refuses to start its bridge if another process already owns port 8972. It never binds to a LAN interface.

## Get the bridge code

Run `Show Hermes Bridge.cmd` from the installed Hermes folder. The generated 256-bit bridge code is stored under the current Windows profile and must be treated like a password. It is not included in the portable installer or repository.

## API

`GET /health` is the only unauthenticated route. The following routes require `Authorization: Bearer <bridge-code>`:

- `GET /v1/session` returns a bounded snapshot of the visible Hermes conversation and tool activity.
- `POST /v1/turns` accepts JSON shaped as `{ "text": "..." }`. Only one bridge turn may run at a time.
- `POST /v1/interrupt` stops the current Hermes turn.

The bridge relays through the Workbench renderer. It does not create a second hidden Hermes session, store a second provider credential, or expose the Docker service directly.
