---
title: "Concepts"
linkTitle: "Concepts"
weight: 20
description: >
  A watch is one task and one subscription. A diff is a JSON artifact.
---

Sentinel does not answer a question and stop. A watch stays open until it
expires or you cancel it. Later result changes arrive on the same A2A task.

## One task, one subscription

The opening message selects one curated query. The host binds the reaction
already listed for that query. You do not choose the reaction, and you do not
send Cypher.

That pair is the task's only subscription. A second watch is a new task. If
you send another watch on a task that already has a subscription, the host
starts a new task and keeps the first watch.

A mapping answer stays on the task that asked. A lifetime answer stays there
too.

## What a diff means

Every Drasi query diff is one JSON artifact. Read `data.type`. A change has
type `native_query_diff`.

- `diffType` `Add` means the row entered the result set.
- `diffType` `Update` means a row still in the set changed.
- `diffType` `Delete` means the row left the result set.

`data` is the row when the host has it. `before` and `after` appear only when
the host supplied them. Do not send another request to fill a missing field.
`queryDescription` is the catalog description of that query.

A status update is not a change. Text parts are status.

## The stream is the delivery

Open the watch with `message/stream`. Keep the response open for the life of
the watch. While the host waits, it writes an SSE comment, `: ping`, at least
every 15 seconds. A comment has no id and is not an event. Do not set a read
timeout.

Each event has an id. Keep the last id you applied. If the stream drops, call
`tasks/subscribe` with that id in `Last-Event-ID`. The host replays events
after that id, then waits. Omit the header only to replay from the start.

A push URL is optional, and it belongs to that task only. Streaming remains
the primary delivery. The model does not choose the URL.

## Lifetime

A watch has a lifetime. Say it in the opening message, for example `7d` or
`none`. If you do not, the agent asks. Answer on the same task. `none` means
no expiry, unless `service.max_ttl` sets a cap.

A due watch is detached and marked expired. It does not keep receiving diffs.

## Query authoring

Some manifests publish a catalog and stop there. If no skill covers the
request, the agent says so and does not start a watch.

When `service.query_authoring.enabled` is true, the agent can build one query
against the configured sources and then subscribe. It still cannot create a
source, see a password, or choose a destination. The manifest must have
exactly one reaction, because the host binds that reaction to the new query.
