---
title: "Manifest"
linkTitle: "Manifest"
weight: 10
description: >
  The YAML fields a host operator sets.
---

The host loads one YAML file. Published queries and reactions stay in this
file. Source connection settings stay here too. Callers never send them.

This is a complete authoring-off file. The password is a secret name, not the
password. The [accounting manifest](https://github.com/drasi-project/drasi-sentinel/blob/main/examples/accounting-local-demo/sentinel.yaml)
is a running copy of the same shape.

```yaml
agent:
  url: { env: AZURE_OPENAI_ENDPOINT }
  model: { env: AZURE_OPENAI_MODEL }
plugins:
  - ref: source/postgres
  - ref: bootstrap/postgres
  - ref: secret-store/file
secretStore:
  kind: file
  path: secrets/secrets.json
service:
  profile: demo
  a2a:
    authentication:
      mode: anonymous
  query_authoring:
    enabled: false
sources:
  - id: accounting-ledger
    kind: postgres
    config:
      host: 127.0.0.1
      port: 5432
      database: sentinel
      user: sentinel
      password:
        kind: Secret
        name: postgres-password
      tables: [invoices]
      slotName: drasi_accounting_slot
      publicationName: drasi_accounting_publication
      sslMode: disable
      tableKeys:
        - table: invoices
          keyColumns: [invoice_id]
    bootstrap:
      kind: postgres
      host: 127.0.0.1
      port: 5432
      database: sentinel
      user: sentinel
      password:
        kind: Secret
        name: postgres-password
      tables: [invoices]
      slotName: drasi_accounting_slot
      publicationName: drasi_accounting_publication
      sslMode: disable
      tableKeys:
        - table: invoices
          keyColumns: [invoice_id]
queries:
  - id: invoice-paid
    description: Reports invoices that are paid.
    examples:
      - Tell me when an invoice is paid.
    cypher: >-
      MATCH (invoice:invoices)
      WHERE invoice.paid = true
      RETURN invoice.invoice_id AS invoice_id
    sources: [accounting-ledger]
    reactions: [notify-account-owner]
reactions:
  - id: notify-account-owner
```

## agent

`agent.url` and `agent.model` each name an environment variable. The host reads those variables when it loads the file. The example names are `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_MODEL`. A different `env` name is a different variable. If a named variable is unset, the process stops and prints that name.

The endpoint has to be Azure OpenAI or Azure AI Foundry. The host calls the OpenAI Responses API there. It does not call chat completions. If the URL ends in `/chat/completions`, that suffix is removed. A generic OpenAI-compatible server will not work, and the host does not accept an API key.

Authentication is an Entra token for `https://ai.azure.com/.default`. When `service.profile` is `demo`, the host uses the Azure CLI credential. Sign in with `az login` before you start that host. Any other profile uses `DefaultAzureCredential`, and that path does not use the Azure CLI.

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

A query is a [continuous query](https://drasi.io/concepts/continuous-queries/).
It has `id`, `description`, `cypher`, `sources`, and `reactions`. `cypher` is
[continuous query syntax](https://drasi.io/reference/query-language/).
`examples` is optional. Those strings are the utterances that select the
skill. `sources` names [source](https://drasi.io/concepts/sources/) ids
declared in the same file. `reactions` names reaction ids. The host binds one
of those reactions. The caller does not.

A reaction has an `id`. The host uses that id when it attaches the watch.

## What stays private

Source `config` holds host, port, database, user, and a password reference.
Use a `Secret` reference. Do not put the password in the manifest. The agent
does not see connection settings.

Plugin refs, such as `source/postgres`, belong in `plugins`. The host
installs them on startup.
