# Local building comfort demonstration

This is the Sentinel version of the [Drasi building comfort tutorial](https://drasi.io/drasi-server/tutorials/building-comfort/).
PostgreSQL holds one building, three floors, and nine rooms. Each room has
temperature, humidity, and co2. The catalog starts with no queries. The agent
authors one watch per task and reports changes as A2A artifacts. The
committed query is `data/authored-queries/<id>.json`. There is no
dashboard reaction.

PostgreSQL listens on `127.0.0.1:5732`. The Inspector listens on
`http://127.0.0.1:8085/`. Port `5432` belongs to the accounting demo. Port
`5433` belongs to the curbside demo.

```bash
cd examples/building-comfort-local-demo
cp .env.example .env
./demo.sh
```

`./demo.sh` prints the host URL, the Inspector URL, and a status line every 5
seconds while it waits. Host output is `secrets/host.log`. Inspector output is
`secrets/inspector.log`. Plugin builds are installed next to `sentinel.yaml`
and are gitignored. If startup says no compatible `secret-store/file` build,
copy the `libdrasi_*.dylib` files from another local demo in this repo.

Every room starts comfortable: temperature 70, humidity 40, co2 10. That is
comfort level 46. A level below 40 or above 50 is uncomfortable. The level is
50 plus the distance from 72 and 42, plus `(co2 - 500) / 25` when co2 is above
500, rounded down.

After a watch is active, push Floor 01 out of band:

```bash
docker exec drasi-sentinel-building-comfort psql -v ON_ERROR_STOP=1 -U sentinel -d building_comfort \
  -c "UPDATE \"Room\" SET temperature = 40, humidity = 20, co2 = 700 WHERE floor_id = 'floor_01_01'"
```

`room_01_01_02` enters the room watch. `floor_01_01` enters the floor watch.
`building_01` enters the building watch, because the average of the three floor
levels falls below 40.

```bash
./e2e.sh
```

The end-to-end script uses `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_MODEL`
from this directory's `.env` or from the accounting demo `.env`.
