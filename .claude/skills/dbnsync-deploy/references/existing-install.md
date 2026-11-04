# A machine that already runs DbnSync

Never start again from scratch here. The archive holds days that Databento no longer offers at the plan's $0 (order books older than about 31 days and L1 older than about 364 on the Standard plan), so a lost archive may cost real money to rebuild, or may not be rebuildable at all.

## Look first

Before changing anything, report what you find. All of this only reads:

```sh
docker compose ls
docker compose ps
docker compose images
scripts/check-env.sh .env
```

Compare their `docker-compose.yml` with this repository's, and tell them what differs.

## Update in place

RustFS, Seq, the archive and `.env` stay as they are. Only the worker changes.

1. **Back up first** if this update crosses a version with changes to the archive, or if they have never backed up. Copy through S3 with `rclone sync` or `aws s3 sync`, to a second disk or bucket. Never copy RustFS's data folder. If there isn't room for it all, keep the oldest order-book days first, since those are the ones money can't easily replace.
2. **Check the new readers can read it,** if you can: `dbnf ls` and `dbnf coverage` with a read-only key.
3. Note the image in use, so that they can go back to it.
4. Put the new `docker-compose.yml` beside the old one, keeping a copy of the old, and set `DBNSYNC_IMAGE` if they aren't on the published image. An install from before the rename runs `ghcr.io/squideyes/dbnsync`; the new file moves it to `ghcr.io/squideyes/dbnsync-worker`.
5. Update only the worker, then repeat step 6 of the skill:

   ```sh
   docker compose pull dbnsync
   docker compose up -d --no-deps dbnsync
   ```

6. To go back, restore the old file and image and run the same command.

## A trial install beside a real one

To try a fresh install on a machine that already runs DbnSync, keep it apart from the real one in every way, because the two would otherwise share containers, ports and a Databento account. The Compose file is named `dbnsync`, so a plain `docker compose` in a new folder acts on the real install unless the project name below is set.

Follow steps 2 to 6 of the skill, with these changes. The real install's own folder is left alone throughout.

1. **Use a new folder**, such as `/volume1/docker/dbnsync-trial`, with `s3` inside it for the archive.
2. **Set these in its `.env`** before anything starts. Nothing here is a secret. Check the ports are free with `ss -ltn`.

   ```
   COMPOSE_PROJECT_NAME=dbnsync-trial
   S3_BUCKET=dbn-trial
   S3_DATA_PATH=/volume1/docker/dbnsync-trial/s3
   S3_PORT=19000
   S3_CONSOLE_PORT=19001
   DBNSYNC_PORT=18080
   SEQ_PORT=15341
   HTTPS_PORT=18443
   DBNSYNC_LOGS_URL=http://<server>:15341
   DBNSYNC_ROOTS=MGC
   ```

   `S3_DATA_PATH` matters most. The template's default is where a real install usually keeps its archive, and a second RustFS on that folder could damage it. `scripts/check-env.sh` fails if the folder or the project name belongs to another install, so run it.
3. **Expect it to grow.** One root with the default schemas stored 1.8 GB in its first 12 minutes, with the backfill unfinished. For less, cut `DBNSYNC_SCHEMAS`.
4. **Pull before stopping anything:** `docker compose pull`. The published image may be newer than the real install's, so the trial may not be testing the same version.
5. **Check the real worker is idle.** `curl -s http://localhost:8080/status` should show `"running":false` and no `jobsPending`. If not, wait for it.
6. **Stop only the real worker,** in its own folder: `docker compose stop dbnsync`. RustFS and Seq keep running. Its dashboard is down until it starts again, including through Caddy, so tell them. It catches up when started again, so keep the stop well inside 31 days.
7. **Start the trial,** in its folder: `docker compose up -d`. Check it on `DBNSYNC_PORT`. It is healthy once `stored:` lines appear in its logs, which takes a second pass, about eight minutes. With spending off, each copy keeps its job ledger in its own bucket and never takes up the other's jobs.
8. **Never run both workers at once.** Stop the trial's first: `docker compose stop dbnsync` in its folder.
9. **Start the real worker again,** in its folder, as part of the trial: `docker compose start dbnsync`. Use `start`, not `up`, which may recreate it.
10. **Prove the archive was left alone.** Its next pass should show `jobsOwed` 0, and this should print 0, with `<time>` as when you stopped it, such as `2026-11-03 22:27:00`:

    ```sh
    find <real S3_DATA_PATH>/<bucket> -newermt '<time>' | wc -l
    ```

    Don't go by the size of the data folder. RustFS's own `.rustfs.sys` moves by itself.

Step 7 of the skill doesn't apply to a trial.

## Removing a trial

Check the project name before you run anything. These remove data for good, so have the person run them.

```sh
docker compose -p dbnsync-trial down -v
sudo rm -rf /volume1/docker/dbnsync-trial
```

`down -v` removes the containers and the Seq and scratch volumes. It doesn't touch the archive folder, which is why the second command is there; it needs `sudo` because user 10001 owns those files. Jobs the trial left at Databento need no clearing up.
