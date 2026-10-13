# Drasi Sentinel .NET handoff

Start this repository as the working-directory root:

```bash
cd drasi-sentinel
git submodule update --init --recursive
```

`a2a-inspector/` is a separate fork of `a2aproject/a2a-inspector`. Its
`origin` is the project fork and its `upstream` is the public inspector.
Commit and push inspector changes from that directory. Do not fold its files
into this repository.

Run the .NET tests:

```bash
dotnet test Drasi.Sentinel.slnx
```

Run the inspector tests:

```bash
cd a2a-inspector
./scripts/test.sh
```

Run the local PostgreSQL CDC demo:

```bash
cd examples/accounting-local-demo
./demo.sh
```

The demo uses fixed local PostgreSQL settings: `127.0.0.1:5432`,
database/user `sentinel`, password `sentinel-demo`. It starts the forked
Inspector at `http://127.0.0.1:8081`.

Run the local curbside demo:

```bash
cd examples/curbside-local-demo
./demo.sh
```

That demo uses PostgreSQL on `127.0.0.1:5433`, MySQL on `127.0.0.1:3309`, and
Inspector port `8083`. It starts those two sources and no queries. Plugin refs
are declared in `sentinel.yaml` and installed on startup. The script prints
the prompts that ask the agent to build each watch.

## Product constraints

- A change watch is one durable A2A task with one curated subscription.
- A subscription selects one query and the host-bound reaction only. Do not add
  filters, confirmation codes, or model-selected destinations. A consuming agent
  may register one push notification URL for that task with the standard A2A
  push notification config methods. Streaming remains the primary contract, and
  the model does not choose the URL.
- When `service.query_authoring.enabled` is true, the agent may create a query
  against the manifest's sources. Sources stay curated. The model does not see
  connection settings, and the new query still becomes one task subscription.
- Report every Drasi query diff as a JSON A2A artifact.
- Use standard A2A JSON-RPC/SSE streaming and `SubscribeToTask` for consumer
  delivery and replay. Do not add a proprietary transport as the primary
  contract.
- Keep example vocabulary and behavior under `examples/` and example fixtures.
  Generic runtime code under `src/` must remain domain-neutral.
