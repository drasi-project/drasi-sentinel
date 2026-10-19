---
title: "Host restart"
linkTitle: "Restart"
weight: 30
description: >
  A restart drops the open stream. The watch and its journal stay on disk.
---

A restart closes every open stream and leaves the watch in the journal. The
process is stopping, so it writes no final SSE event. You see a dropped
connection, not a cancelled watch.

The new process reads the data directory, reattaches each watch that had not
already ended, and only then starts the sources. The old HTTP response does
not resume. Open `tasks/subscribe` again with the last event id you applied. The calls
are in [Keep the stream open](../../guides/keep-the-stream/).

## Files the new process opens

The journal is `data/journal.sqlite`, next to the manifest unless
`service.data` points elsewhere. It holds the tasks, subscriptions, task
events, authored queries, diff receipts, and push configs. The reconnect
replays from this file.

The engine store is separate. Query indexes are under `data/indexes`.
Reaction checkpoints and plugin state are in `data/state.redb`. `data/wal`
is the engine log for a transient source. That log does not resume a Postgres
source or a MySQL source. Postgres keeps its read position in the replication
slot named by that source. MySQL keeps a binlog position. Those positions
survive process exit only if the database still has that history.

At startup the host rewrites `data/authored-queries/` from the journal. The
engine loads authored Cypher from the journal, not from those files. Catalog
Cypher still comes from the manifest.

## Watches that come back

Before the host listens, it expires watches that are already due. It then
reattaches each subscription in `Arming`, `Active`, or `Degraded`. Sources
start after that reattach.

A cancelled watch stays cancelled. An expired watch stays expired. `Paused`
and `Revoked` would also stay detached, but this host does not write those
states. A client that waits for `Paused` or `Revoked` waits forever.

A watch that was `Arming` or `Degraded` is saved as `Active`, then attached.
A successful attach does not write a new subscription event. If attach fails,
that watch is marked `Degraded`. The journal records
`Subscription {id} could not be reattached`, followed by the error. The other
watches still come back.

## What the new stream contains

`Last-Event-ID` is the journal sequence, the SSE `id` you applied. It is not
`querySequence`. The host replays journal events after that id, then waits.
A missing or invalid header replays from the start.

A change already in the journal is in the replay when its id is after your
cursor. A change the source still has, and that the old process never
journaled, is not in that replay. It arrives after the new process starts the
source. Leave the new stream open so that artifact can arrive.

A `: ping` comment is not stored. The new stream does not replay pings from
the old process.

If your cursor is already past a terminal event, the replay does not include
that event, and the stream waits. If the replay includes `TaskCompleted` or
`TaskFailed` and nothing follows, the response ends.

## Changes while the host was down

A row already delivered stays one journal row. A row inserted while the host
was down is journaled once after the source resumes, if the database still
has the log the checkpoint needs.

The host does not skip a gap. If the replication slot or the binlog cannot
satisfy the checkpoint, that watch does not jump ahead.

## Push after a restart

Push does not fill a gap the source could not satisfy. The URL for the task
is still stored, but the host does not walk old artifacts and post them again
at startup. A post that failed, or that was in flight when the process
exited, is not retried. The artifact remains in the journal. Read it on the
reconnected stream.

A diff the new process ingests is posted when that task still has a push
config.

## What does not come back

The journal and the indexes come back. A reply the agent had not finished is
gone. The next message you send starts a new turn. That turn can see
caller text and assistant text already in the journal. It cannot see a draft
the old turn never journaled.

A query probe does not resume. A leftover probe index is deleted at startup.

A second host cannot open the same data directory while the first process is
alive. `data/state.redb` stays locked until that process exits.

## A replay is not a second change

Reconnecting can show you an artifact you already applied. Seeing it again
does not mean the row changed again. The host stores a receipt of `querySequence` and `diffOrdinal` in the same
transaction as the event. A repeat of that receipt commits nothing new.

Deleting `data/indexes` while keeping the journal is a different failure. A
rebuilt query can emit a new sequence. The old receipt does not collapse that
emission.
