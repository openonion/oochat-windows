# ConnectOnion Conversation Interaction and Session Message Structure

> This document summarizes the interaction model based on the ConnectOnion WebSocket Protocol and
> Session Reconnect documentation, together with real `sessions` JSON data captured from a live agent.
> It is tailored to a WinUI 3 + C# + SQLite desktop client.

## How to read this document (status)

Sections **1–31 describe the core wire protocol** — what the agent sends and expects for
`CONNECT` / `INPUT` / `OUTPUT`, streaming, `session_id`, reconnect, and `last_msg_id`. They are
authoritative for the frames they specify. The protocol has since gained extension frames that are
implemented and tested but are not expanded into full standalone sections below; the current
dispatch table in `ConnectOnion.Protocol/AgentConnectionService.cs` is authoritative for that
supported set.

Current implemented frame coverage:

| Category | Frames handled by the client |
|---|---|
| Connection and terminal | `CONNECT`, `CONNECTED`, `OUTPUT`, `ERROR`, `PING`/`PONG` |
| Input and control | `INPUT` (also used as runtime input while a turn is active), `INTERRUPT`, `mode_change`, `mode_changed`, `SESSION_STATUS`, `RUNTIME_INPUT_ACK` |
| Interactive | `ask_user`, `approval_needed`, `plan_review` |
| Onboarding | `ONBOARD_REQUIRED`, `ONBOARD_SUBMIT`, `ONBOARD_SUCCESS` |
| Streamed activity | `thinking`, `llm_call`, `llm_result`, `tool_call`, `tool_result`, `assistant`, `intent`, `eval`, `compact`, `tool_blocked` |
| Attachments and generated content | `agent_image`, `files_received`, `diff_preview` |
| Reconnect/state synchronization | `session_sync`, `last_msg_id`, `session_id` |

Unknown frame types are ignored rather than treated as terminal output. Adding a new supported
frame therefore requires updating the dispatch table, protocol tests, the projection mapping when
it is user-visible, and this coverage table.

Pre-implementation recommendations for DTO layers, SQLite schema, client mapping, and page-owned
connection state have been removed because the shipped architecture diverged from them. Use
[`AGENTS.md`](../AGENTS.md) or [`CLAUDE.md`](../CLAUDE.md) for the current application architecture.

Where the protocol is implemented in this repo:

- Connection state machine, timeouts (`ConnectTimeoutMs = 30_000`, `SilenceTimeoutMs = 60_000`) — `ConnectOnion.Protocol/AgentConnectionService.cs`
- `INPUT` frame construction (text / images / files / mixed) — `ConnectOnion.Protocol/InputMessageBuilder.cs`
- Interactive turns (`ask_user`, `approval_needed`, `plan_review`) — `ConnectOnion.Protocol/AgentInteractiveParsers.cs`
- Attachment wire events + data-URL codec — `ConnectOnion.Protocol/AttachmentModels.cs`
- Ed25519 / canonical-JSON signing — `AgentIdentity.cs`, `CanonicalJson.cs`, verified byte-for-byte against the Node reference signer by `ConnectOnion.Protocol.Conformance`

---

## 1. Core Concepts

A complete ConnectOnion conversation flow should distinguish four concepts:

| Concept | Meaning | Lifetime |
|---|---|---|
| WebSocket Connection | A concrete network transport connection | Ends when the socket closes |
| Session | A logical remote conversation and execution context | Can survive multiple INPUT/OUTPUT cycles and WebSocket reconnects |
| Execution | One `INPUT -> OUTPUT` agent run | Ends when the final OUTPUT is produced |
| Conversation | The user's long-term chat history | Usually persisted by the client |

The core relationship is:

```text
Conversation
    │
    └── Session
          │
          ├── Execution 1
          │     INPUT
          │       ↓
          │     events
          │       ↓
          │     OUTPUT
          │
          ├── Execution 2
          │     INPUT
          │       ↓
          │     events
          │       ↓
          │     OUTPUT
          │
          └── Execution 3
                ...
```

A single Session can also span multiple WebSocket connections:

```text
Session abc-123
    │
    ├── WebSocket #1
    │      ↓ disconnect
    ├── WebSocket #2
    │      ↓ disconnect
    └── WebSocket #3
```

Therefore:

```text
One message = one Session                         ❌
One WebSocket connection = one permanent Session ❌
One INPUT -> OUTPUT cycle = one Execution         ✅
Multiple Executions can belong to one Session     ✅
One Session can survive WebSocket reconnects      ✅
```

---

## 2. Overall Interaction Flow

A normal new conversation flow:

```text
Client                                      Agent Server
  │                                             │
  │── WebSocket Open ──────────────────────────►│
  │                                             │
  │── CONNECT ─────────────────────────────────►│
  │   auth + optional session_id + session      │
  │                                             │
  │◄─ CONNECTED ────────────────────────────────│
  │   session_id + status                       │
  │                                             │
  │◄─ PING ─────────────────────────────────────│
  │── PONG ────────────────────────────────────►│
  │                                             │
  │── INPUT ───────────────────────────────────►│
  │   prompt / images / files                   │
  │                                             │
  │◄─ thinking ─────────────────────────────────│
  │◄─ tool_call ────────────────────────────────│
  │◄─ tool_result ──────────────────────────────│
  │◄─ OUTPUT ───────────────────────────────────│
  │   result + session                          │
  │                                             │
  │── INPUT ───────────────────────────────────►│
  │   next prompt                               │
  │                                             │
  │◄─ ... ──────────────────────────────────────│
  │◄─ OUTPUT ───────────────────────────────────│
```

After `CONNECTED`, the same WebSocket connection may send multiple `INPUT` messages.

---

## 3. How `session_id` Is Created

### 3.1 Creating a New Session

When a client starts a new Session, it may omit `session_id`:

```json
{
  "type": "CONNECT",
  "session": {
    "messages": []
  },
  "payload": {
    "to": "0xAgentAddress",
    "timestamp": 1702234567
  },
  "from": "0xClientPublicKey",
  "signature": "0x..."
}
```

The server returns:

```json
{
  "type": "CONNECTED",
  "session_id": "550e8400-...",
  "status": "new"
}
```

The normal model is:

```text
Client omits session_id
        ↓
Server creates a new Session
        ↓
Server assigns session_id
        ↓
Client stores session_id
```

### 3.2 Resuming an Existing Session

On reconnect, the client sends the previously stored `session_id`:

```json
{
  "type": "CONNECT",
  "session_id": "550e8400-...",
  "last_msg_id": "ev-9f12...",
  "session": {
    "messages": []
  },
  "payload": {
    "to": "0xAgentAddress",
    "timestamp": 1702234567
  },
  "from": "0xClientPublicKey",
  "signature": "0x..."
}
```

The server checks the registry state:

```text
session_id exists + running
    → status = "running"

session_id exists + idle
    → status = "connected"

session_id missing from registry
    → status = "new"
```

The current protocol documentation also describes a fresh-session path when a provided `session_id` is not found in the active registry.

---

## 4. CONNECT Message

`CONNECT` is the first protocol message on a WebSocket connection.

### 4.1 Structure

```json
{
  "type": "CONNECT",
  "session_id": "550e8400-...",
  "last_msg_id": "ev-9f12...",
  "session": {
    "messages": [],
    "mode": "safe"
  },
  "payload": {
    "to": "0x3d4017c3e843...",
    "timestamp": 1702234567
  },
  "from": "0xClientPublicKey",
  "signature": "0x..."
}
```

### 4.2 Fields

| Field | Required | Meaning |
|---|---|---|
| `type` | Yes | Fixed value: `CONNECT` |
| `session_id` | No | Existing Session to resume |
| `last_msg_id` | No | Last stream event fully processed by the client |
| `session` | No | Client-side Session snapshot, including messages |
| `payload` | Yes | Signed authentication payload |
| `from` | Yes | Client public-key address |
| `signature` | Yes | Signature |

### 4.3 Responsibilities

CONNECT may combine:

```text
Authentication
+
Session creation or resume
+
Client conversation-state synchronization
+
Potential server-to-client Session synchronization
```

---

## 5. CONNECTED Message

The server responds to CONNECT with a CONNECTED message:

```json
{
  "type": "CONNECTED",
  "session_id": "550e8400-...",
  "status": "new",
  "server_newer": true,
  "session": {
    "messages": []
  },
  "chat_items": []
}
```

### 5.1 `status`

| Status | Meaning | Client Action |
|---|---|---|
| `new` | Fresh Session or old Session no longer active | Ready to send INPUT |
| `connected` | Session exists and is idle | Ready to send INPUT |
| `running` | Agent execution is still active | Wait for replay/live events |

### 5.2 `server_newer`

When the server has a newer Session snapshot:

```json
{
  "server_newer": true,
  "session": {
    "messages": []
  },
  "chat_items": []
}
```

The client should refresh its local UI/state accordingly.

---

## 6. INPUT Message

After CONNECTED, the client can send INPUT.

### 6.1 Plain Text

```json
{
  "type": "INPUT",
  "prompt": "check my latest unread email"
}
```

### 6.2 With Images

```json
{
  "type": "INPUT",
  "prompt": "Analyze this image",
  "images": [
    "data:image/png;base64,..."
  ]
}
```

### 6.3 With Files

```json
{
  "type": "INPUT",
  "prompt": "Summarize this PDF",
  "files": [
    {
      "name": "report.pdf",
      "data": "data:application/pdf;base64,..."
    }
  ]
}
```

### 6.4 Fields

| Field | Required | Meaning |
|---|---|---|
| `type` | Yes | Fixed value: `INPUT` |
| `prompt` | Yes | User input |
| `images` | No | Base64 data-URL images |
| `files` | No | Inline base64 file objects |

Important:

```text
INPUT does not need to resend the full Session.
```

The current WebSocket has already been associated with a Session by CONNECT.

---

## 7. One Execution Message Flow

A typical Execution:

```text
INPUT
  ↓
user_input
  ↓
thinking
  ↓
llm_call
  ↓
llm_result
  ↓
optional tool_call
  ↓
tool_result
  ↓
optional next llm_call
  ↓
llm_result
  ↓
OUTPUT
```

Example:

```text
User: "check my latest unread email"
       ↓
thinking
       ↓
LLM decides to call read_inbox
       ↓
tool_call
       ↓
tool_result: "No emails found."
       ↓
thinking / reflect
       ↓
LLM final response
       ↓
OUTPUT
```

---

## 8. Streaming Event Structure

During execution, the server may emit events before final OUTPUT.

Common event types include:

| Type | Meaning |
|---|---|
| `thinking` | Intermediate agent state or reasoning event |
| `tool_call` | Tool invocation started |
| `tool_result` | Tool execution completed |
| `ask_user` | Agent requires more user input |
| `approval_needed` | Tool action requires approval |
| `plan_review` | A plan is waiting for review |
| `compact` | Context compaction event |

Conceptual examples:

```json
{
  "type": "thinking",
  "content": "I will check the inbox."
}
```

```json
{
  "type": "tool_call",
  "name": "read_inbox",
  "args": {
    "unread": true,
    "last": 1
  }
}
```

```json
{
  "type": "tool_result",
  "name": "read_inbox",
  "status": "success",
  "result": "No emails found."
}
```

The actual `trace` objects in the provided data are richer and may also include:

```text
id
ts
session_id
iteration
duration_ms
usage
model
status
```

---

## 9. OUTPUT Message

An Execution ends with OUTPUT:

```json
{
  "type": "OUTPUT",
  "result": "It looks like you don't have any unread emails.",
  "session_id": "550e8400-...",
  "duration_ms": 1250,
  "session": {
    "messages": [],
    "trace": [],
    "turn": 2
  }
}
```

Important:

```text
OUTPUT = the current Execution is complete
OUTPUT ≠ the Session is destroyed
```

The same Session can continue:

```text
OUTPUT #1
   ↓
INPUT #2
   ↓
OUTPUT #2
   ↓
INPUT #3
   ↓
OUTPUT #3
```

---

## 10. Actual `sessions` List Structure

The real JSON discussed in this conversation has this top-level shape:

```json
{
  "sessions": [
    {
      "session_id": "a985fbf5-...",
      "status": "done",
      "prompt": "ddadada",
      "result": "...",
      "session": {
        "session_id": "a985fbf5-...",
        "messages": [],
        "trace": [],
        "turn": 2,
        "user_prompt": "ddadada",
        "intent": "...",
        "iteration": 1,
        "result": "...",
        "updated": 1783233428.7857492
      },
      "created": 1783233420.4608629,
      "expires": 1783319820.4608629,
      "duration_ms": 8322
    }
  ]
}
```

It can be decomposed as:

```text
sessions[]
  │
  └── Result Record
        ├── session_id
        ├── status
        ├── prompt
        ├── result
        ├── created
        ├── expires
        ├── duration_ms
        │
        └── session
              ├── session_id
              ├── messages[]
              ├── trace[]
              ├── turn
              ├── user_prompt
              ├── intent
              ├── iteration
              ├── result
              └── updated
```

---

## 11. Outer `sessions[]` Item

Example:

```json
{
  "session_id": "a985fbf5-4b70-4b61-b835-c90524c1e9b4",
  "status": "done",
  "prompt": "ddadada",
  "result": "I'm still not sure...",
  "session": {},
  "created": 1783233420.4608629,
  "expires": 1783319820.4608629,
  "duration_ms": 8322
}
```

Suggested interpretation:

| Field | Meaning |
|---|---|
| `session_id` | Unique Session identifier |
| `status` | Result/execution-record status, e.g. `done` |
| `prompt` | Latest prompt associated with the record |
| `result` | Final result |
| `session` | Full Session snapshot |
| `created` | Record creation timestamp |
| `expires` | Result expiration timestamp |
| `duration_ms` | Execution duration |

### 11.1 Do Not Mix the Two Status Models

Outer result data:

```json
{
  "status": "done"
}
```

WebSocket CONNECTED:

```json
{
  "status": "new"
}
```

or:

```json
{
  "status": "connected"
}
```

or:

```json
{
  "status": "running"
}
```

These are different state domains.

Recommended interpretation:

```text
sessions[] outer status
    = result/execution-record state

CONNECTED.status
    = current remote Session state
```

---

## 12. `session.messages` Structure

`messages` represents conversation context used by the agent/LLM.

### 12.1 User Message

```json
{
  "role": "user",
  "content": "hi"
}
```

### 12.2 Assistant Text Message

```json
{
  "role": "assistant",
  "content": "Hi there! How can I help you today?"
}
```

### 12.3 Assistant Tool-Call Message

Actual structure from the discussed data:

```json
{
  "role": "assistant",
  "tool_calls": [
    {
      "id": "call_00_ixRY6qOmP6GPgSPvfbFg4111",
      "type": "function",
      "function": {
        "name": "count_unread",
        "arguments": "{}"
      }
    },
    {
      "id": "call_01_ghogJPc3qRMXvNzIRQT05719",
      "type": "function",
      "function": {
        "name": "read_inbox",
        "arguments": "{\"unread\": true, \"last\": 5}"
      }
    }
  ]
}
```

Important:

```text
function.arguments is a JSON string.
```

It is not necessarily a nested JSON object.

For example:

```json
"arguments": "{\"unread\": true, \"last\": 5}"
```

A client may need to deserialize it a second time for structured display.

### 12.4 Tool Result Message

```json
{
  "role": "tool",
  "content": "You have 0 unread email(s).",
  "tool_call_id": "call_00_ixRY6qOmP6GPgSPvfbFg4111"
}
```

Relationship:

```text
assistant.tool_calls[].id
        │
        └── tool.tool_call_id
```

Example:

```text
call_00_ixRY...
    │
    ├── function = count_unread
    │
    └── result = "You have 0 unread email(s)."
```

---

## 13. `session.trace` Structure

`trace` is closer to a full execution log than to chat history.

Typical order:

```text
user_input
  ↓
thinking
  ↓
llm_call
  ↓
llm_result
  ↓
tool_call
  ↓
tool_result
  ↓
thinking
  ↓
llm_call
  ↓
llm_result
```

---

## 14. `trace.user_input`

```json
{
  "type": "user_input",
  "content": "hi",
  "turn": 1,
  "ts": 1783153056.7559657,
  "id": "5cd51b66-fd20-4dce-91a5-0b754d550d15",
  "session_id": "a985fbf5-4b70-4b61-b835-c90524c1e9b4"
}
```

Fields:

| Field | Meaning |
|---|---|
| `type` | `user_input` |
| `content` | User input |
| `turn` | Conversation turn number |
| `ts` | Timestamp |
| `id` | Trace event ID |
| `session_id` | Owning Session |

---

## 15. `trace.thinking`

```json
{
  "type": "thinking",
  "kind": "intent",
  "content": "Hi there! How can I help you today?",
  "id": "d1c7edad-...",
  "ts": 1783153058.9878283,
  "session_id": "a985fbf5-..."
}
```

Or:

```json
{
  "type": "thinking",
  "kind": "reflect",
  "content": "No unread emails found...",
  "id": "...",
  "ts": 1783153065.6221685,
  "session_id": "..."
}
```

The discussed data includes at least:

```text
kind = intent
kind = reflect
```

---

## 16. `trace.llm_call`

```json
{
  "type": "llm_call",
  "id": "6219dc6e-fa23-4aa8-8eee-e5e8e508ce48",
  "model": "deepseek-v4-pro",
  "iteration": 1,
  "status": "running",
  "ts": 1783153058.98922,
  "session_id": "a985fbf5-..."
}
```

Fields:

| Field | Meaning |
|---|---|
| `model` | Model name |
| `iteration` | Current agent/LLM iteration |
| `status` | Call status |
| `ts` | Start timestamp |

---

## 17. `trace.llm_result`

```json
{
  "type": "llm_result",
  "id": "6219dc6e-fa23-4aa8-8eee-e5e8e508ce48",
  "model": "deepseek-v4-pro",
  "iteration": 1,
  "duration_ms": 3622.2217082977295,
  "tool_calls_count": 2,
  "usage": {
    "input_tokens": 1791,
    "output_tokens": 228,
    "cached_tokens": 0,
    "cache_write_tokens": 0,
    "cost": 0
  },
  "context_percent": 1.39921875,
  "status": "success",
  "ts": 1783153062.6115417,
  "session_id": "a985fbf5-..."
}
```

This trace type is useful for a desktop performance/diagnostics page:

```text
LLM latency
token usage
context usage
tool-call count
model
cost
success/failure
```

---

## 18. `trace.tool_call`

```json
{
  "type": "tool_call",
  "tool_id": "call_00_ixRY6qOmP6GPgSPvfbFg4111",
  "name": "count_unread",
  "args": {},
  "id": "6af03411-cc8f-4c71-a26a-d163856abe6f",
  "ts": 1783153062.6132116,
  "session_id": "a985fbf5-..."
}
```

Another example:

```json
{
  "type": "tool_call",
  "tool_id": "call_01_ghogJPc3qRMXvNzIRQT05719",
  "name": "read_inbox",
  "args": {
    "unread": true,
    "last": 5
  },
  "id": "5100d36c-...",
  "ts": 1783153063.0672123,
  "session_id": "a985fbf5-..."
}
```

---

## 19. `trace.tool_result`

```json
{
  "type": "tool_result",
  "tool_id": "call_00_ixRY6qOmP6GPgSPvfbFg4111",
  "name": "count_unread",
  "args": {},
  "status": "success",
  "result": "You have 0 unread email(s).",
  "timing_ms": 452.43144035339355,
  "id": "4ad79c91-...",
  "ts": 1783153063.065727,
  "session_id": "a985fbf5-..."
}
```

A UI could display:

```text
Tool: count_unread
Status: success
Time: 452 ms
Result: You have 0 unread email(s).
```

---

## 20. `messages` vs `trace`

### `messages`

Purpose:

```text
LLM Conversation Context
```

Contains:

```text
user
assistant
tool call
tool result
assistant
```

Characteristics:

- Closer to model context
- Useful for conversation restoration
- Useful for constructing subsequent requests

### `trace`

Purpose:

```text
Execution Observability / Debugging
```

Contains:

```text
user_input
thinking
llm_call
llm_result
tool_call
tool_result
```

Characteristics:

- More detailed
- Contains timing
- Contains token usage
- Contains iteration
- Contains model
- Contains status
- Contains event IDs

Therefore:

```text
messages = what happened in the conversation
trace    = how the agent produced the result
```

---

## 21. `turn` vs `iteration`

### `turn`

Represents the user conversation turn.

Example:

```text
Turn 1:
User: hi
Assistant: hello

Turn 2:
User: check inbox
Assistant: no unread email

Turn 3:
User: hi
Assistant: ...
```

Corresponding state:

```json
{
  "turn": 3
}
```

### `iteration`

Represents the internal LLM/agent loop count within an Execution.

Example:

```text
INPUT
  ↓
LLM iteration 1
  ↓
tool_call
  ↓
tool_result
  ↓
LLM iteration 2
  ↓
final answer
```

Therefore:

```text
turn = 3
iteration = 2
```

is completely valid.

---

## 22. Runtime Input

The current protocol supports sending another INPUT while the agent is already running.

Example:

```text
INPUT #1
   ↓
Agent running
   ↓
Client sends INPUT #2 before OUTPUT
```

Instead of necessarily starting a second concurrent agent, the server can treat it as runtime input:

```json
{
  "type": "RUNTIME_INPUT_ACK",
  "session_id": "550e8400-...",
  "id": "runtime-input-7c2a..."
}
```

Conceptual flow:

```text
Agent is running
   ↓
New INPUT arrives
   ↓
Input is incorporated into a later agent iteration
   ↓
RUNTIME_INPUT_ACK
   ↓
The original Execution eventually produces OUTPUT
```

This is relevant to a desktop client's interruption, stop-response, and mid-execution-input design.

---

## 23. Ask User and Approval

### 23.1 Agent Requests User Input

Server event:

```text
ask_user
```

Client response:

```json
{
  "type": "ASK_USER_RESPONSE",
  "answer": "Python 3"
}
```

### 23.2 Tool Approval

Server event:

```text
approval_needed
```

Client response:

```json
{
  "type": "APPROVAL_RESPONSE",
  "approved": true,
  "scope": "once"
}
```

---

## 24. PING / PONG

Server:

```json
{
  "type": "PING"
}
```

Client:

```json
{
  "type": "PONG"
}
```

The current documentation describes a periodic heartbeat.

Purpose:

```text
keep connection alive
+
update last_ping
+
support Session cleanup decisions
```

---

## 25. WebSocket Disconnect and Session Reconnect

Normal model:

```text
Session abc-123
    │
    ├── WebSocket #1
    │      │
    │      └── disconnect
    │
    ├── Agent thread may survive
    ├── IO queues may be retained
    ├── events may be buffered
    │
    └── WebSocket #2
           │
           └── CONNECT { session_id: "abc-123" }
                    ↓
              registry.get(...)
                    ↓
                 FOUND
                    ↓
             Reattach to old Session
```

Reconnect during execution:

```text
Client                                    Server
  │                                         │
  │── WS Open ─────────────────────────────►│
  │                                         │
  │── CONNECT ─────────────────────────────►│
  │   session_id                            │
  │   last_msg_id                           │
  │   session                               │
  │                                         │
  │◄─ CONNECTED ────────────────────────────│
  │   status = running                     │
  │                                         │
  │◄─ replay missed events ─────────────────│
  │◄─ live events ──────────────────────────│
  │◄─ OUTPUT ───────────────────────────────│
```

---

## 26. `last_msg_id`

Reconnect example:

```json
{
  "type": "CONNECT",
  "session_id": "abc-123",
  "last_msg_id": "ev-9f12..."
}
```

Meaning:

```text
The client tells the server:
"I have fully processed events up to this event ID."
```

The server can then:

```text
rewind_to(last_msg_id)
    ↓
replay only later missed events
```

This helps avoid duplicate UI items such as:

```text
duplicate thinking events
duplicate tool_call events
duplicate tool_result events
duplicate chat items
```

---

## 27. Session Merge

The client and server may both hold Session snapshots.

Example:

```text
Client Session
iteration = 5

Server Session
iteration = 10
```

The documented merge strategy can be summarized as:

```text
Higher iteration wins
```

Examples:

```text
Client 5 vs Server 10
    → Server wins

Client 8 vs Server 3
    → Client wins
```

If iteration is equal:

```text
The more recently updated snapshot wins
```

The discussed Session data includes:

```json
{
  "iteration": 1,
  "updated": 1783233428.7857492
}
```

So these fields are useful for both display and synchronization/conflict resolution.

---

## 28. Session Lifecycle

A practical lifecycle summary:

```text
register
   ↓
running
   │
   ├── agent finishes
   │      ↓
   │   completed / idle
   │
   └── WebSocket disconnect
          ↓
       session survives
          ↓
       reconnect
          ↓
       running again
```

A simplified WebSocket-protocol state model:

```text
new
 ↓ CONNECT
connected
 ↓ INPUT
running
 ↓ agent done
connected
```

---

## 29. Session Storage

The current reconnect documentation describes two broad layers.

### 29.1 In Memory

```text
ActiveSessionRegistry
```

Conceptually stores:

```text
session_id
  ↓
{
  io,
  thread,
  status,
  last_ping
}
```

Used for:

```text
running agent
IO queues
thread state
reconnect
live execution state
```

### 29.2 Result Persistence

The documentation describes final-result persistence under:

```text
.co/session_results.jsonl
```

Used for:

```text
client does not reconnect in time
        ↓
later GET /sessions/{id}
        ↓
recover final result
```

The current documentation describes a roughly 24-hour result-retention window.

---

## 30. `expires` in the Actual Data

The discussed data includes:

```json
{
  "created": 1783233420.4608629,
  "expires": 1783319820.4608629
}
```

Difference:

```text
1783319820.4608629
-
1783233420.4608629
=
86400 seconds
=
24 hours
```

This is consistent with the documented 24-hour result-retention behavior.

---

## 31. `sessions[]` Result Data Is Not the Same as WebSocket Frames

The actual data:

```json
{
  "sessions": [
    {
      "status": "done",
      "prompt": "...",
      "result": "...",
      "session": {}
    }
  ]
}
```

is better understood as:

```text
Session Result Query / Result Storage API
```

rather than as the raw real-time WebSocket frame stream.

Recommended separation:

```text
WebSocket DTOs
    CONNECT
    CONNECTED
    INPUT
    OUTPUT
    PING
    PONG
    Stream Events

Session Result DTOs
    session_id
    status = done
    prompt
    result
    session
    created
    expires
    duration_ms
```

---

## 32. Final Structural Summary

The complete system can be viewed as:

```text
Client
│
├── Local Conversation
│   ├── conversation_id
│   ├── title
│   ├── messages
│   └── remote_session_id
│
├── WebSocket Connection
│   ├── CONNECT
│   ├── INPUT
│   ├── PONG
│   ├── ASK_USER_RESPONSE
│   └── APPROVAL_RESPONSE
│
└── Receive
    ├── CONNECTED
    ├── PING
    ├── thinking
    ├── tool_call
    ├── tool_result
    ├── ask_user
    ├── approval_needed
    ├── RUNTIME_INPUT_ACK
    ├── OUTPUT
    └── ERROR

Server
│
├── ActiveSessionRegistry
│   └── session_id
│       ├── io
│       ├── thread
│       ├── status
│       └── last_ping
│
├── Agent Execution
│   ├── user_input
│   ├── thinking
│   ├── llm_call
│   ├── llm_result
│   ├── tool_call
│   └── tool_result
│
└── Result Storage
    └── .co/session_results.jsonl
```

In one sentence:

> **Conversation is the durable client-side chat history; Session is the resumable remote logical context; WebSocket is the temporary transport connection; Execution is one INPUT-to-OUTPUT agent run; `messages` stores conversation context; and `trace` stores the detailed execution path.**

---

## 33. Sources

- ConnectOnion WebSocket Protocol  
  https://docs.connectonion.com/websocket-protocol
- ConnectOnion Session Reconnect  
  https://docs.connectonion.com/session-reconnect
- Actual `sessions` JSON sample provided in this conversation
