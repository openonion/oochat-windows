# ConnectOnion Windows Desktop Client Requirements

This document records the public product requirements and maps each one to its
implementation. The traceability table is the current status, including known
limitations.

## Requirement → Implementation Traceability

### Core functional requirements

| # | Requirement | Status | Where |
|---|---|---|---|
| 1 | Agent connection, status display, graceful failure | ✅ | `ConnectOnion.Protocol/AgentConnectionService.cs` (WebSocket state machine, 30s connect timeout, 60s silence watchdog), `EndpointResolver` (relay lookup + direct probe + `/info`), `ConnectionTester`, `AgentPresenceService`; offline/retry surfaces in `ChatViewModel` + `OfflineNoticeBar` |
| 2 | Desktop chat interaction | ✅ | `Views/ChatPage.xaml`, `Controls/Chat/ChatComposer`, per-bubble `DataTemplate`s dispatched by `Common/ChatMessageTemplateSelector` |
| 3 | Streaming responses / progress feedback | ✅ | `ConnectOnion.WinUIClient.Core/Services/Runtime/ChatTurnProjection.cs` maps `thinking` / `tool_call` / `tool_result` / `assistant` / … to bubbles; `Controls/Chat/ToolActivityView` renders the per-turn tool timeline |
| 4 | Conversation & session management | ✅ | `SessionRepository`, `ConversationRepository` (SQLite), agent → session tree in `Controls/Shell/ShellSidebar` |
| 5 | Agent configuration management | ✅ | `AgentRepository`, `Controls/Agents/AddAgentForm`, `Views/AgentDetailPage` |
| 5b | Secure handling of credentials | ✅ | `Data/IdentityStore.cs` — the Ed25519 seed is DPAPI-protected (`CurrentUser`) at rest, never plaintext; an unreadable identity is now reported to the user rather than silently replaced |
| 6 | Windows desktop UX (layout, keyboard, long responses, settings, light/dark) | ✅ | Custom title bar + `ShellSidebar`; `Services/KeyboardShortcutCatalog` + `KeyboardShortcutsDialog`; `MarkdownTextBlock` for long responses; `SettingsOverlay`; full Light/Dark token system in `Styles/Colors.xaml` + `Styles/Brushes.xaml` |
| 7 | Reliability: interruption, timeout, failure, invalid config, reconnect | ✅ | Timeouts and `ConnectionLost` in `AgentConnectionService`; failed/cancelled runs persist their partial output plus an error bubble (`AgentSessionManager.PersistFailedAsync`); manual reconnect via `ChatViewModel.ReconnectAsync` |
| 8 | Testing and documentation | ✅ | Docs: `CLAUDE.md`, this `docs/` folder. CI runs Protocol, Core/architecture, and SQLite suites with a coverage ratchet plus `ConnectOnion.Protocol.Conformance`. A 36-test FlaUI shell/chat suite runs on every push and pull request; see [`TEST_PLAN.md`](./TEST_PLAN.md). |

### Optional stretch goals

| Goal | Status | Where |
|---|---|---|
| System tray integration | ✅ | `Shell/MainWindow.Tray.cs`, `Shell/MainWindow.Tray.Interop.cs` |
| Desktop notifications for completed tasks | ✅ | `Services/Notifications/` — `NotificationCoordinator` + `NotificationPolicy` decide suppress / in-app toast / system toast; clicking one routes to the right conversation, including from a cold start |
| Multiple-agent workspace management | ✅ | Multiple agents, each with its own conversations; per-conversation connections isolated by `AgentConnectionRegistry` |
| File drag-and-drop | ✅ | `Services/Attachments/AttachmentDropService.cs`; `Controls/Chat/ChatComposer` is the drop target |
| Keyboard shortcuts | ✅ | `MainWindow.{File,Edit,View,Help}Menu.cs`; the catalog is surfaced in-app via Ctrl+Shift+/ |
| Installer packaging / release-ready build | ✅ **Portable channel** | `.github/workflows/release.yml` publishes a gated x64 self-contained portable ZIP from `v*` tags. Signed MSIX is a deferred future channel, not a requirement of the current portable release. |

### Known limitations (as of this commit)

- WinUI coverage above the headless surfaces now includes 36 required FlaUI shell/chat tests on every push and pull request. Explorer drag-and-drop remains skipped because its source coordinates vary with desktop DPI/layout; OS notification delivery/click and direct tray-icon interaction remain manual (see `TEST_PLAN.md`).
- The portable ZIP release pipeline publishes from matching semantic-version
  tags, beginning with `v0.1.1`. Clean-profile validation remains a manual gate
  for each new release; signed MSIX publishing remains paused.
- App XAML exposes 158 unique `AutomationId`s; `AutomationContractTests` guards the 53 critical real-window locators and rejects any named interactive control without an ID. Each new real-window flow still adds its app-owned locator to the critical subset.
- English and Simplified Chinese resource sets exist and are key-parity gated; manual zh-CN layout, text-scaling, and Narrator validation remain open.

---

## Project background & goals

ConnectOnion is an open-source AI agent framework that enables users to build and interact with remotely accessible AI agents. Existing access to ConnectOnion agents is primarily provided through web-based or command-line interfaces.

The goal of this project is to design and implement a Windows desktop client that allows users to interact with ConnectOnion agents through a dedicated desktop application. The application should provide a convenient and reliable experience for Windows users, supporting agent connection, chat interaction, local conversation management and desktop-oriented usability features.

The outcome will be a functional Windows application prototype that extends the ConnectOnion ecosystem beyond browser-based access and provides a foundation for future desktop productivity features.

## Project scope

This project focuses on the design and development of a Windows desktop client application for ConnectOnion.

Contributors should investigate the existing ConnectOnion open-source framework, documentation, web client and relevant communication mechanisms. Based on this analysis, they should design an appropriate Windows desktop application architecture and implement an end-to-end client.

The minimum project scope includes:

- Development of a Windows desktop application using an appropriate technology stack.
- Connection to one or more remotely accessible ConnectOnion agents.
- A desktop chat interface for sending user messages and displaying agent responses.
- Support for response progress, streaming updates or execution status where supported.
- Local conversation/session history.
- Saved agent connection configurations.
- Secure handling of required credentials or connection information.
- Desktop-appropriate navigation, settings, status display and error handling.
- Testing, technical documentation and a demonstrable Windows build.

The project is not intended to be only a packaged version of the existing web interface. It should provide a desktop-oriented application experience suitable for ongoing extension and use on Windows.

Optional extensions may include system tray integration, desktop notifications, multiple agent workspaces, drag-and-drop file input, keyboard shortcuts and installable packaging.

## Project requirements

Contributors are expected to design and implement a functional, maintainable and testable Windows desktop client for accessing ConnectOnion AI agents.

### Core functional requirements

#### 1. Agent Connection

The application must allow users to configure and connect to remote ConnectOnion agents using an appropriate address, endpoint or connection configuration. Connection status should be clearly displayed and invalid or unavailable connections should be handled gracefully.

#### 2. Desktop Chat Interaction

Users must be able to send messages to a connected agent and receive responses through a usable desktop chat interface. The interface should support message display, input handling, scrolling, loading feedback and response status indicators where appropriate.

#### 3. Response and Task Feedback

Where supported by the ConnectOnion communication interface, the application should display streaming responses or progress information. Users should receive clear feedback while an agent request is running, completed or failed.

#### 4. Conversation and Session Management

The application should support local conversation history and multiple chat sessions. Users should be able to start new conversations, reopen previous conversations and organise interactions with different agents.

#### 5. Agent Configuration Management

Users should be able to save, select, edit and remove agent connection configurations. Sensitive information, where required, should be handled using appropriate secure storage mechanisms available on Windows.

#### 6. Windows Desktop User Experience

The application should provide a desktop-oriented user experience, including appropriate window layout, keyboard and mouse interaction, readable presentation of long responses, settings management and support for light/dark display where feasible.

#### 7. Reliability and Error Handling

The application should handle network interruption, request timeout, failed agent responses, invalid configuration and reconnection scenarios with meaningful user-facing feedback.

#### 8. Testing and Documentation

The project should include testing for key functionality, including communication logic, conversation persistence, configuration handling and major user workflows. The final delivery should include source code, build instructions, architecture documentation and known limitations.

### Minimum deliverables

- Windows desktop application source code.
- A demonstrable executable or installable Windows build.
- Successful interaction with at least one ConnectOnion agent.
- Conversation history and agent configuration management.
- User-friendly connection and error handling.
- Testing evidence and technical documentation.
- Release notes and maintenance documentation.

### Optional stretch goals

- System tray integration.
- Desktop notifications for completed agent tasks.
- Multiple-agent workspace management.
- File drag-and-drop support.
- Keyboard shortcuts.
- Installer packaging and release-ready build.

## Required knowledge and skills

Contributors should possess or be willing to develop skills in:

- Software engineering, version control, testing and technical documentation.
- Desktop application development using an appropriate framework.
- Client-server communication, APIs, asynchronous programming and network error handling.
- User interface and user experience design for desktop software.
- Git-based collaborative development workflows.

Desirable but not essential skills include:

- Experience with C#/.NET, WinUI, WPF, Electron, Tauri or comparable desktop development technologies.
- Experience with WebSocket communication or real-time applications.
- Experience with local persistence and secure configuration storage.
- Familiarity with AI applications or agent-based systems.
- Experience with application packaging or Windows deployment.

## Expected outcomes and deliverables

- Source code
- Documentation
- User guide

## Disciplines related to the project

- Software Development
- Generative AI (GenAI)
- Human Computer Interaction (HCI)

## Resources provided

- OpenOnion architecture source code
- Selected LLM APIs
