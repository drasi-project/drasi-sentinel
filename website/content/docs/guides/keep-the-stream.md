---
title: "Keep the stream open"
linkTitle: "Keep the stream open"
weight: 20
description: >
  Leave the SSE stream open, and reconnect with the last event id.
---

A watch can stay open for hours or days. The HTTP response is the delivery
channel. Do not close it because it has been idle.

## What an idle stream looks like

The host writes an SSE comment at least every 15 seconds:

```text
: ping
```

A comment has no `id` line and is not an event. Do not treat it as a diff.
Do not set a read timeout on this stream. A timeout will drop a healthy watch.

Each real event has an `id` line and a `data` line. The id is a decimal
sequence. Store the last id you applied.

`data` is a JSON-RPC result. Read `result.kind`.

- `status-update` is not a change.
- `artifact-update` is an output. A change is a data part whose `type` is
  `native_query_diff`.

## Reconnect

If the stream drops, POST `tasks/subscribe` with the same task id. Set the
`Last-Event-ID` header to the last id you applied. The host replays events
after that id, then waits.

Omit the header only when you want the journal from the start.

Inspector calls this **Resume task stream**.

## After the host restarts

The old response is closed. POST `tasks/subscribe` again with the same task
id and the last id you applied. The host does not resume the old HTTP
response.

[Host restart](../../concepts/restart/) says what the journal keeps, which
watches come back, and what a change during downtime does.

## Push is optional

To receive the same artifact updates without holding the stream, POST
`tasks/pushNotificationConfig/set` with that task id and an absolute `http`
or `https` URL. You can also set
`configuration.taskPushNotificationConfig` on the opening `message/stream`.

That URL belongs to that task only. A failed post does not drop the journaled
artifact. Streaming remains the primary delivery. The model does not choose
the URL.
