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

## Product constraints

- A change watch is one durable A2A task with one curated subscription.
- A subscription selects a curated query and reaction only. Do not add
  filters, confirmation codes, destinations, or webhook delivery.
- Report every Drasi query diff as a JSON A2A artifact.
- Use standard A2A JSON-RPC/SSE streaming and `SubscribeToTask` for consumer
  delivery and replay. Do not add a proprietary transport as the primary
  contract.
- Keep example vocabulary and behavior under `examples/` and example fixtures.
  Generic runtime code under `src/` must remain domain-neutral.
