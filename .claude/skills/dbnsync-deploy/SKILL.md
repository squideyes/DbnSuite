---
name: dbnsync-deploy
description: Install, update, check or troubleshoot DbnSync, the Docker service that archives Databento data to S3, on a NAS (Synology, QNAP, UGREEN), a Linux server, Docker Desktop or a DigitalOcean Droplet. Use this whenever someone wants to deploy, set up, run, upgrade, move or repair DbnSync or its RustFS, Seq or Caddy containers, give readers S3 keys, or reach the archive from the internet, even if they only say "get this running on my NAS" or "the dashboard is down".
---

# Deploy DbnSync

DbnSync is three containers: RustFS (S3, holding the archive), Seq (logs) and the worker. This skill takes someone from nothing to a healthy service, then helps them keep it that way. Be prescriptive: use the defaults unless the person has a reason not to, and don't offer a menu.

The people using this are often traders, not system administrators. Say what a step does in plain words before you do it, and explain any failure in terms of what they should do next.

## Say this first

Before anything else, tell the person, in your own words but keeping all three points:

- DbnSuite is an independent project with no official connection with Databento, Inc.
- It is for data they are licensed to receive; their Databento agreements govern storing, sharing and redistributing it.
- It is free software, provided as is, and they run it at their own risk.

## Rules that protect the person

These exist because the worker acts on a real Databento account and the archive can take days to rebuild.

- **Keep secrets out of the conversation.** Anything shown to you leaves their machine. Ask them to type `DATABENTO_API_KEY` into `.env` themselves, in an editor. Don't print `.env`, don't `grep` its values, and don't echo a secret. `scripts/check-env.sh` reports on it without showing any value.
- **Never turn on spending.** Leave `DBNSYNC_ALLOW_PAID_JOBS` and the `DBNSYNC_MAX_*` settings alone: the default of $0 is what makes this safe. If someone wants paid jobs, send them to the [spend policy docs](https://dbnsuite.squideyes.com/docs/dbnsync#spend) and have them set it themselves, knowing Databento bills their account. When jobs are refused as over the limit, the fix is the plan, `DbnSync__HistoryStart`, `DbnSync__ReachBackTo` or the schemas, never a higher limit.
- **Protect the archive.** Never run `docker compose down -v` on a real install, delete or `chown` a folder that already holds data without looking at it first and asking, or copy files into RustFS's data folder (it keeps its own on-disk format; move data through S3 only).
- **Ask before anything that costs money or exposes the service:** creating a Droplet or Space, opening a port, changing a router or DNS.
- **Don't open ports 8080, 9000, 9001 or 5341 to the internet.** The dashboard has no sign-in of its own.

## 1. Find out where it is going

Ask only what you can't see for yourself:

1. **Which machine?** A NAS, a Linux server, Docker Desktop, or a DigitalOcean Droplet. For anything but a plain Linux server, read `references/targets.md` now; it changes several steps below.
2. **Are you on that machine?** If not, run each command over `ssh <host>` and copy files with `scp` (`scp -O` on UGOS). Ask for the host name, never a password; SSH keys or their own prompt handle sign-in.
3. **Is DbnSync already there?** Look, don't just ask: run `docker compose ls` and `docker ps`, and look for a `dbnsync` folder with a `.env`. If it is there this is an update or a repair, not an install: go to step 7 or `references/troubleshooting.md`, and read `references/existing-install.md` before changing anything. The Compose file is named `dbnsync`, so a second copy in another folder takes over the running containers rather than sitting beside them.

Then check the machine and tell them what you found:

```sh
docker --version
docker compose version    # must be v2, with a space
df -h <the disk for the archive>
```

The defaults need about 90 GB on day one and grow about 750 GB a year, nearly all of it order books. If the disk is short, cut the roots or schemas rather than letting it fill. They need a Databento plan with history from a supported dataset; today that is CME Globex (`GLBX.MDP3`).

## 2. Get the deployment files

From a checkout of this repository, copy `Deploy/docker-compose.yml`, `Deploy/Caddyfile` and `Deploy/.env.example` to the target's folder, the last as `.env`. Otherwise:

```sh
mkdir -p ~/dbnsync && cd ~/dbnsync
curl -fsSLO https://raw.githubusercontent.com/squideyes/DbnSuite/main/Deploy/docker-compose.yml
curl -fsSLO https://raw.githubusercontent.com/squideyes/DbnSuite/main/Deploy/Caddyfile
curl -fsSL https://raw.githubusercontent.com/squideyes/DbnSuite/main/Deploy/.env.example -o .env
chmod 600 .env
```

Copy this skill's `scripts/` beside them when you are working over SSH.

## 3. Make the archive folder

RustFS runs as user id 10001, so that user has to own the folder. Put it on the disk you checked.

```sh
sudo mkdir -p /srv/dbnsync/s3
sudo chown -R 10001:10001 /srv/dbnsync/s3
```

## 4. Fill in .env

Run `scripts/fill-secrets.sh .env`. It generates the S3 secret and Seq's password straight into the file, so neither passes through this conversation.

Set these yourself, since none is a secret:

- `S3_DATA_PATH`: the folder from step 3.
- `DBNSYNC_LOGS_URL`: Seq as their browser reaches it, such as `http://myserver:5341`.
- `DBNSYNC_ROOTS` and `DBNSYNC_SCHEMAS`, only if they want other than the defaults (twelve CME index, energy and metal roots; `mbo,ohlcv-1s,definition,statistics,status`).

Then ask them to open `.env` and paste their Databento key after `DATABENTO_API_KEY=`, and to say when that's done. It comes from the API keys page of their Databento portal and starts `db-`.

Run `scripts/check-env.sh .env` and fix whatever it marks `FAIL`. Treat `PAID` as a stop: tell them spending is on and ask whether they meant that.

## 5. Start it

```sh
docker compose pull
docker compose up -d
```

If `DBNSYNC_IMAGE` names an image already loaded on this machine, there is nothing to pull: use `docker compose pull s3 seq` first.

## 6. Prove it works

Don't call it done until you have seen each of these, because a container that is merely up may still be refusing every job:

```sh
docker compose ps                        # all three running, s3 healthy
curl -s http://localhost:8080/health     # answers Healthy
curl -s http://localhost:8080/status     # the worker's state
docker compose logs --tail 50 dbnsync    # a pass has started; no refusals
```

Then report in plain words: the dashboard's address (`http://<server>:8080`), where the archive lives, and what happens next. With the defaults the first backfill is about 670 Databento jobs, at most 30 at a time, and takes a day or two; the dashboard's ETA gives the time to expect.

If anything fails, use `references/troubleshooting.md`.

## 7. Afterwards

Offer these, in this order, and do only what they accept:

1. **Separate keys.** Until this is done the worker uses the S3 administrator's key, which can delete the archive. `references/keys.md`.
2. **Reach it from elsewhere.** `references/internet.md`. Recommend Tailscale unless they need to share with people outside their own devices.
3. **Set a Seq retention policy.** Seq keeps everything until told otherwise; 30 days is plenty, in its Settings.

To **update**, follow `references/existing-install.md`. Stopping loses nothing: the next pass picks up whatever was pending, and the archive is the only state.
