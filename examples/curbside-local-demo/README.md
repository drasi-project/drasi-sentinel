# Build curbside watches from sources

You start two sources and no queries. Orders live in PostgreSQL. Vehicles live
in MySQL. Sentinel builds each curbside watch from a prompt, one A2A task at a
time. A later change on either database arrives on that task as a
`native_query_diff`.

`sentinel.yaml` declares the plugin refs, including `secret-store/file`, the
same way Drasi Server does. On startup, Sentinel installs those plugins and
registers the file secret store. Bootstrap writes owner-only
`secrets/secrets.json`. Source passwords reference names in that file. There
is no separate lock script.

## Start the demo

Create `.env` from `.env.example`, or export the same two variables. The
accounting demo `.env` uses those names.

```bash
cd examples/curbside-local-demo
./demo.sh
```

PostgreSQL listens on `127.0.0.1:5433`. MySQL listens on `127.0.0.1:3309`.
Inspector is at `http://127.0.0.1:8083/`.
The script prints the Agent Card URL. Port `5432` belongs to the accounting
demo. This demo does not use it.

Plates `A1234`, `B5678`, and `C9876` are preparing, and those vehicles are in
Parking. Two other rows are there so the stored states can be read from the
source: order `F1000` is `ready`, and vehicle `G2000` is `Curbside`. Those
rows do not share a plate, so they do not satisfy the matched watch.

## Ask for each watch

Start a new Inspector task for each prompt. One task watches one query. Click
**Send streaming**. Wait until Inspector shows that the subscription is active
before you start the next task.

The script prints these prompts:

1. An order that is still being prepared.
2. An order that is ready for pickup.
3. A vehicle in the parking lot.
4. A vehicle at the curb.
5. A ready order whose vehicle, with the same plate, is at the curb.
6. A vehicle that has waited at the curb for 10 seconds while its order is
   still being prepared.

The prompts describe the situation. They do not include Cypher. The agent
reads the sources and builds the query. The committed definition is
`data/authored-queries.json`.

## Drive a change

After the ready-order task is active, run:

```bash
docker exec drasi-sentinel-curbside-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d retail \
  -c "UPDATE orders SET status = 'ready' WHERE plate = 'A1234'"
```

The ready task shows a `native_query_diff` with `diffType` `Add` for plate
`A1234`. The preparing task, if you opened one, shows `Delete` for that plate.

After the matched task is active, run:

```bash
docker exec -e MYSQL_PWD=sentinel-demo drasi-sentinel-curbside-mysql mysql -usentinel physical \
  -e "UPDATE vehicles SET location = 'Curbside' WHERE plate = 'A1234'"
```

The matched task shows an `Add` for plate `A1234`.

For the delay task, leave order `B5678` as `preparing` and run:

```bash
docker exec -e MYSQL_PWD=sentinel-demo drasi-sentinel-curbside-mysql mysql -usentinel physical \
  -e "UPDATE vehicles SET location = 'Curbside' WHERE plate = 'B5678'"
```

That diff is scheduled for 10 seconds after the vehicle has been at the
curbside. It does not arrive on the update. If it does not arrive, the order
row was not in the query yet. Run this, then wait 10 seconds:

```bash
docker exec drasi-sentinel-curbside-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d retail \
  -c "UPDATE orders SET status = 'preparing' WHERE plate = 'B5678'"
```

Press Ctrl+C to stop Sentinel, Inspector, and both database containers.

To run the ready and matched prompts against the configured model and check
the diffs, run `./e2e.sh` from this directory.
