---
title: "Manifest"
linkTitle: "Manifest"
weight: 10
description: >
  The YAML fields a host operator sets.
---

The host loads one YAML file. Queries and reactions are curated assets. Source
connection settings stay in this file. Callers never send them.

```yaml
service:
  profile: demo
  data: data
  max_ttl: 30d
  a2a:
    authentication:
      mode: anonymous
  query_authoring:
    enabled: false
queries:
  - id: overdue-invoices
    description: Finds open invoices whose due date has passed.
    examples:
      - Tell me as soon as an invoice is overdue.
    cypher: MATCH (invoice:Invoice) RETURN invoice.id AS invoice_id
    sources: [invoices]
    reactions: [notify-account]
reactions:
  - id: notify-account
```

## service

`profile` is `demo`, `development`, or `production`. Anonymous authentication
is valid only when `profile` is `demo`. A JWT host sets `mode` to `jwt` and
supplies `issuer`, `audience`, and `public_key`.

`data` is the directory for the journal, Drasi indexes, and authored queries.
It is relative to the manifest, or absolute. The default is `data` next to
the manifest. Two hosts must not share that directory. Exit one process
before starting another against the same files.

`max_ttl` is the longest lifetime a caller may choose. Omit it, or set
`none`, for no cap. A value is `none` or a positive integer plus `s`, `m`,
`h`, or `d`.

`query_authoring.enabled` must be `true` or `false`. `true` requires exactly
one reaction. The agent may then create queries against the declared sources.
It cannot create sources or choose a destination.

## queries and reactions

A query has `id`, `description`, `cypher`, `sources`, and `reactions`.
`examples` is optional. Those strings are the utterances that select the
skill. `sources` names source ids declared in the same file. `reactions`
names reaction ids. The host binds one of those reactions. The caller does
not.

A reaction has an `id`. The host uses that id when it attaches the watch.

## What stays private

Source `config` holds host, port, database, user, and a password reference.
Use a `Secret` reference. Do not put the password in the manifest. The agent
does not see connection settings.

Plugin refs, such as `source/postgres`, belong in `plugins`. The host
installs them on startup.
