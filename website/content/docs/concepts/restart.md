---
title: "Host restart"
linkTitle: "Restart"
weight: 30
description: >
  A restart drops the open stream. The watch and its journal stay.
---

A process restart closes every open HTTP response. It does not end the watch.
The subscription, the task journal, and the query indexes stay on disk. The
new process reopens them, reattaches each watch that has not ended, and then
starts the sources.

The old SSE response is gone. To catch up, open a new `tasks/subscribe` stream
and send the last event id you applied. The steps are in
[Keep the stream open](../../guides/keep-the-stream/).

## What stays on disk

The journal is `data/journal.sqlite`, next to the manifest unless
`service.data` points elsewhere. It holds the tasks, subscriptions, task
events, authored queries, diff receipts, and push configs.

The engine store is separate. Query indexes are under `data/indexes`.
Reaction checkpoints and plugin state are in `data/state.redb`. `data/wal` is
the engine log for a transient source. It does not resume a Postgres or MySQL
source.

Postgres keeps its read position in the replication slot named by that
source. MySQL keeps a binlog position. Those positions survive process exit
only if the database still has that history.

Files under `data/authored-queries/` are rewritten from the journal at
startup. The engine loads authored Cypher from the journal, not from those
files. Catalog Cypher still comes from the manifest.

## Which watches come back

Before the host listens for HTTP, it expires due watches. It then reattaches
each subscription whose state is `Arming`, `Active`, or `Degraded`. Sources
start after that reattach.

A cancelled watch does not come back. An expired watch does not come back.
`Paused` and `Revoked` would also stay detached. This host does not write
those two states.

A watch that was `Arming` or `Degraded` is saved as `Active`, then attached.
A successful attach does not write a new subscription event. If attach fails,
that watch is marked `Degraded`. The journal gets
`Subscription {id} could not be reattached`, followed by the error. The other
watches continue.

## What the client sees

Shutdown does not write a final SSE event because the process is stopping.
The client sees a dropped connection.

`Last-Event-ID` is the journal sequence, the SSE `id` you applied. It is not
`querySequence`. The host replays journal events after that id, then waits.
A missing or invalid header replays from the start.

A change already in the journal is in that replay if its id is after your
cursor. A change the source still holds, and that was not journaled before
the exit, arrives after the new process starts the source. Keep the new
stream open. That artifact shows up on it.

A `: ping` comment is not stored. The new stream does not replay pings from
the old process.

If your cursor is already past a terminal event, the new stream does not see
that event, and it waits. If the replay includes `TaskCompleted` or
`TaskFailed` and nothing follows, the response ends.

## Changes during downtime

A row that was already delivered stays one journal row. A row inserted while
the host was down is journaled once after the source resumes, if the database
still has the log the checkpoint needs.

The host does not skip a gap. If the slot or the binlog cannot satisfy the
checkpoint, that watch does not jump ahead.

## Push

The push URL for a task is still stored after restart. The host does not walk
old artifacts and post them again at startup. A post that failed, or that was
in flight at the exit, is not retried. The artifact remains in the journal.
Read it by reconnecting the stream.

A diff ingested after the new process starts is posted when that task still
has a push config.

## What does not resume

An in-flight model turn does not resume. The next user message starts a new
turn. History comes from the caller text and the assistant text that were
already journaled.

A query probe does not resume. A leftover probe index is deleted at startup.

A second host against the same data directory cannot open the state file
while the first process is still alive. The state file stays locked until
that process exits.

## Duplicates

A replay of a diff that was already journaled does not create a second
artifact. The host stores a receipt of `querySequence` and `diffOrdinal` in
the same transaction as the event. A repeat of that receipt commits nothing
new.

Deleting `data/indexes` while keeping the journal is a different failure. A
rebuilt query can emit a new sequence. The old receipt does not collapse that
emission.
