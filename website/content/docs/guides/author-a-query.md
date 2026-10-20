---
title: "Author a query"
linkTitle: "Author a query"
weight: 50
description: >
  Describe a change that is not already a published skill.
---

Use this page when the host has `service.query_authoring.enabled` set to
`true`. That setting requires exactly one reaction. The host binds that
reaction to the query the agent commits. If the flag is false or absent, a
miss does not become a watch. The agent says the catalog does not cover it
and stops.

A published skill still wins when your message matches one. [Start a watch](../start-a-watch/)
is that path. This page is the miss.

## Send the description

Send the description as the whole user text on a new task. Include the
lifetime. Do not send the query text. [Start a watch](../start-a-watch/) lists
the other fields a message must not contain.

This is a message that opens a watch on the [curbside](../examples/curbside/)
host:

```text
Tell me when a pickup order is ready for pickup. Keep the watch for 7 days.
```

The agent reads the schema, samples stored values, and tries the query before
it subscribes. [Building comfort](../examples/building-comfort/) is the other
host that works this way. A condition you define in the message is part of
the query. It is not a stored field the agent should ask you to map.

## If the agent asks

Some words are not stored values. This message does not open a watch on the
curbside host:

```text
Tell me when an order is ready and the customer is waiting. Keep the watch for 7 days.
```

`waiting` is not a column. The agent asks which stored value that word refers
to. Answer on the same task. That answer is not a new watch.

## What the agent will not do

The agent cannot connect a new database, insert rows, or see a connection
password. It writes one query against sources already in the manifest.

A committed query is written to `data/authored-queries/<id>.yaml`. The file
has the id, description, query text, sources, and joins. The agent does not
repeat that text on the task. Later diffs use the same `native_query_diff`
artifact as a published query.
