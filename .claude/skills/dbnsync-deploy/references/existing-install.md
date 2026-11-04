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
4. Put the new `docker-compose.yml` beside the old one, keeping a copy of the old, and set `DBNSYNC_IMAGE` if they aren't on the published image.
5. Update only the worker, then repeat step 6 of the skill:

   ```sh
   docker compose pull dbnsync
   docker compose up -d --no-deps dbnsync
   ```

6. To go back, restore the old file and image and run the same command.

## A trial install beside a real one

To try a fresh install on a machine that already runs DbnSync, keep it apart from the real one in every way, because the two would otherwise share containers, ports and a Databento account:

- Use a new folder, and give every command a project name of its own: `docker compose -p dbnsync-trial ...`.
- Use a new archive folder and a new `S3_BUCKET`, and never the real `S3_DATA_PATH`.
- Set `S3_PORT`, `S3_CONSOLE_PORT`, `DBNSYNC_PORT` and `SEQ_PORT` to free ports.
- Set `DBNSYNC_ROOTS` to one or two roots, so that the trial is small.
- Stop the real worker for the trial, leaving RustFS and Seq running: `docker compose stop dbnsync` in its folder. It catches up when started again, so keep the stop well inside 31 days. Starting it again afterwards is part of the trial, so don't leave it out.
- When it's over, remove only what the trial made, and check the project name before you run anything: `docker compose -p dbnsync-trial down -v`.
