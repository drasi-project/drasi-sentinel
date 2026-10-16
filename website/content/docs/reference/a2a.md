---
title: "A2A contract"
linkTitle: "A2A contract"
weight: 20
description: >
  Methods, headers, and artifact fields a client uses.
---

The Agent Card is `/.well-known/agent-card.json`. It advertises `JSONRPC`,
`capabilities.streaming: true`, and `capabilities.pushNotifications: true`.
POST JSON-RPC 2.0 to the URL of the `supportedInterfaces` entry whose
`protocolBinding` is `JSONRPC`. Use `Content-Type: application/json` and
`Accept: text/event-stream`.

Do not use `message/send` as the primary call. Use `message/stream`.

## Open a watch

```json
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "message/stream",
  "params": {
    "message": {
      "role": "user",
      "taskId": "task-1",
      "messageId": "msg-1",
      "parts": [
        {
          "mediaType": "text/plain",
          "text": "Tell me when an invoice is paid. Keep the watch for 7 days."
        }
      ]
    }
  }
}
```

`parts` is one text part. A part with `kind` `text` is also accepted.

## Methods

| Method | Use |
| --- | --- |
| `message/stream` | Send a message and hold the SSE response. |
| `tasks/subscribe` | Reconnect to a task. Set `Last-Event-ID` to the last id you applied. |
| `tasks/list` | List this caller's tasks. `params` is `{}`. |
| `tasks/get` | Read one task. `params.id` is the task id. |
| `tasks/cancel` | End that watch. |
| `tasks/pushNotificationConfig/set` | Register one absolute URL for that task. |

`ListTasks`, `GetTask`, `CancelTask`, and `SubscribeToTask` are the same
operations under their A2A names.

## Artifact fields

A change is an artifact data part whose `type` is `native_query_diff`.

| Field | Meaning |
| --- | --- |
| `subscriptionId` | The task's subscription. |
| `queryId` | The curated or authored query. |
| `queryDescription` | The catalog description. |
| `reactionId` | The reaction the host bound. |
| `occurredAt` | When the host recorded the diff. |
| `querySequence` | The query's sequence number. |
| `diffOrdinal` | The diff's position in that result. |
| `diffType` | `Add`, `Update`, or `Delete`. |
| `data` | The row, when present. |
| `before`, `after` | Present only when the host supplied them. |
| `groupingKeys` | Present when the host supplied them. |
| `metadata` | Present when the host supplied it. |

A current-results artifact has `type` `current_query_results`. It contains
`taskId`, `subscriptionId`, `queryId`, `queryDescription`, and `rows`.

Null fields are omitted.
