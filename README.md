# OOChat for Windows

Native Windows client for [ConnectOnion](https://github.com/openonion/connectonion) agents.
Connect to an agent by its `0x…` address and talk to it — the same protocol the
[web client](https://github.com/openonion/oo-chat) speaks.

[![Windows Build](https://github.com/openonion/oochat-windows/actions/workflows/build.yml/badge.svg)](https://github.com/openonion/oochat-windows/actions/workflows/build.yml)

## Who this is for

This is a starting point, not a finished product you are meant to use as-is.

If you are running ConnectOnion agents and your users want a native Windows app
rather than a browser tab, fork this, change the four things listed under
[Make it yours](#make-it-yours), and ship it. The web client is the default
answer; this exists for when the default is not enough.

The source is open so you can change it. We would rather you shipped your own
build than waited for ours.

## What it does

- Connect to a hosted agent by `0x…` address over the ConnectOnion relay
- Ed25519 request signing — the app holds its own keypair, the agent authorises it
- BIP39 seed phrase for identity backup and restore
- Streamed replies rendered as they arrive
- Multi-turn sessions kept on device, in local SQLite
- Tool-approval prompts: allow once, trust for this session, decline, stop the
  whole task, or ask the agent to explain why it wants the tool
- Voice input with on-device activity detection
- System tray presence and desktop notifications
- English and Simplified Chinese UI

### What it does not do

- **No code signing or installer.** The build produces an unsigned app. Shipping
  to end users needs your own certificate and packaging step.
- **No App Store / Microsoft Store submission** is configured.
- **The relay endpoint is not editable in the UI.** It is a constant in the
  protocol layer — see [Configure](#configure).

## Requirements

| | |
|---|---|
| Windows | 10 version 2004 (build 19041) or later |
| Toolchain | .NET SDK 10.0.302 or later, Windows App SDK |
| A running agent | See [connectonion](https://github.com/openonion/connectonion) — `pip install connectonion`, then `co init` |

## Install

```
git clone https://github.com/openonion/oochat-windows.git
cd oochat-windows
dotnet restore
```

## Configure

The server address is the one thing most people need to change.

| What | Where | Default |
|---|---|---|
| Relay endpoint | `ConnectOnion.Protocol/EndpointResolver.cs` (`DefaultRelay`) | `wss://oo.openonion.ai` |
| Agent direct URL | Per agent, on the Add Agent screen | — |
| Invite code | `CO_INVITE_CODE` environment variable | unset |

Point it at your own relay by changing `DefaultRelay` — no rebuild of the
protocol layer's callers is required.

## Run

```
dotnet run --project ConnectOnion.WinUIClient
```

## Build

```
dotnet build --configuration Release
dotnet test  --configuration Release
```

Produces an unsigned WinUI 3 desktop application plus its test results.

## Make it yours

The four places that carry our identity. Change these and it is your app:

| # | What | Where |
|---|---|---|
| 1 | Application identifier | `ConnectOnion.WinUIClient/Package.appxmanifest` — currently `ai.openonion.oochat` |
| 2 | Display name | `ConnectOnion.WinUIClient/Package.appxmanifest`, and `Strings/*/Resources.resw` |
| 3 | Icon and launch image | `ConnectOnion.WinUIClient/Assets/` |
| 4 | Backend endpoint | `ConnectOnion.Protocol/EndpointResolver.cs` (see [Configure](#configure)) |

Colours and type live in `ConnectOnion.WinUIClient/Styles/`. The palette is a
single green accent on a neutral canvas; changing the accent there changes it
everywhere.

## Architecture

Three layers. `ConnectOnion.Protocol` owns the wire protocol — endpoint
resolution, the relay connection, Ed25519 identity, and BIP39 key handling; it
has no UI dependency and is where you look when changing how the app talks to an
agent. `ConnectOnion.WinUIClient.Core` holds the models, services and view models,
including the approval-card state machine and session persistence. The
`ConnectOnion.WinUIClient` project is the WinUI 3 shell: pages, controls, and
styles. Session state is owned by Core and persisted to local SQLite.

## Contributing

Issues and pull requests are welcome at https://github.com/openonion/oochat-windows.

## License

MIT — see [LICENSE](LICENSE).

Copyright (c) 2026 ConnectOnion PTY LTD.
