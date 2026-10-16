---
title: "Author a query"
linkTitle: "Author a query"
weight: 50
description: >
  Describe a change that is not already a published skill.
---

Query authoring is off unless the manifest sets
`service.query_authoring.enabled` to `true`. That setting requires exactly
one reaction. The host binds that reaction to the query the agent commits.

## Describe the change

If no `continuous-query` skill matches, send the description as the whole
user text on a new task. Do not send Cypher, a source name, a filter, or a
destination.

Include the lifetime in that text. The [building comfort](../examples/building-comfort/)
and [curbside](../examples/curbside/) demos are hosts that work this way.

The agent reads the configured schema, samples stored values, and probes the
query before it opens the watch. A condition you define in the message, with
`means` or as an expression, is part of the query. It is not a stored field
the agent should ask you to map.

If the agent asks which stored value a word refers to, answer on the same
task. That answer is not a new watch.

## What gets stored

A committed query is written to `data/authored-queries/<id>.yaml`. The file
has the id, description, Cypher, sources, and joins. The agent does not
repeat that Cypher on the task. Later diffs use the same
`native_query_diff` artifact as a curated query.

The agent cannot create a source, insert rows, or see a connection password.

## When authoring is off

If no catalog query covers the request, the agent says so and stops. It does
not invent a query, and it does not ask for a lifetime.
