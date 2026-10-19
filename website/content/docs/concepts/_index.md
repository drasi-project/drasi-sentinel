---
title: "Concepts"
linkTitle: "Concepts"
weight: 20
description: >
  A watch is one standing task. Later changes arrive on it while the watch stays open.
---

A watch is a standing request. You describe one change. The host opens one
A2A task and keeps that task until the watch expires or you cancel it. Later
changes arrive on that same task.

The host is the process you run. The agent is the part of that process that
reads your message and writes the reply. The subscription and the stream
belong to the task the watch opened.

## One message selects one query

The opening message is the description, in plain text. It is not a skill id
or a reaction id. The agent matches that text to one published query. The
host then binds the reaction the manifest already listed for that query. You
do not choose the reaction, and you do not send Cypher.

That query and that reaction are the subscription. The task keeps that one
subscription. A later message on the task does not add a second subscription.

A second watch is a new task. If you send another watch on a task that
already has a subscription, the host does not replace the first watch. It
opens a new task for the second one.

Some later messages are answers, not watches. If the agent asks which stored
value a word refers to, answer on the task that asked. If it asks how long to
keep the watch, answer there too. A new task would start a new watch, and the
question on the first task would still be waiting.

## A diff is one change to the result set

The query is a [Drasi continuous query](https://drasi.io/concepts/continuous-queries/).
It keeps a result set and reports how that set changed. A diff is not a
fresh copy of the set.

Each change is one JSON artifact. The data part has type `native_query_diff`.
`diffType` says what happened to the row.

- `Add` means the row entered the result set.
- `Update` means a row that is still in the set changed.
- `Delete` means the row left the result set.

`data` is the row, when the host has it. `before` and `after` appear only
when the host had those values. A missing field was not supplied. No later
call fills that field in. `queryDescription` is the catalog text for the query, so you can see which
query produced the artifact.

A status update is the agent talking. Text in that update is not a row
entering or leaving the set. The change is the artifact. Field names are in
the [A2A contract](../reference/a2a/).

If you need the rows already in the set, ask on the same task. That reply is
a different artifact, `current_query_results`, and it does not take the place
of later diffs. The request text is in
[Ask for current results](../guides/current-results/).

## The stream has to stay open

You open the watch with `message/stream`. Leave that response open. It is how
the diffs arrive. The host often has nothing new to send, because the result
set has not changed. While it waits, it writes an SSE comment, `: ping`, at
least every 15 seconds. The comment has no id and is not an event. A read
timeout treats that wait as a failure and drops a watch that is fine.

Each real event has an id. Keep the last id you applied. If the connection
drops, call `tasks/subscribe` and send that id in `Last-Event-ID`. The host
replays events after that id, then waits. Leave the header off only when you
want the journal from the start. The calls are in
[Keep the stream open](../guides/keep-the-stream/).

A host restart closes that HTTP response. The watch is still there.
[Host restart](restart/) says what the new process keeps, and what a
reconnect with your last id still shows.

A push URL is optional, and it belongs to that task only. Set one when you
want the same artifacts posted to an address. The stream remains the delivery
you can replay. You choose the URL, or you leave it unset. The agent will not
pick one.

## The watch runs until its lifetime ends

Say how long to keep the watch in the opening message. `7d` is seven days.
`none` means the watch does not expire, unless `service.max_ttl` sets a cap.
If you leave the lifetime out, the agent asks. Answer on the same task. The
tokens are in [Set a lifetime](../guides/set-a-lifetime/).

When the lifetime is due, the host detaches the subscription and marks the
task expired. That task stops receiving diffs.

## A request the catalog does not cover

If no published query covers the message, the agent says so and stops. It
does not invent a query.

When `service.query_authoring.enabled` is true, the agent can write one query
against the sources already in the manifest, then subscribe to it. It still
cannot create a source, read a password, or choose a destination. The
manifest must contain exactly one reaction, because the host binds that
reaction to the new query. The procedure is in
[Author a query](../guides/author-a-query/).
