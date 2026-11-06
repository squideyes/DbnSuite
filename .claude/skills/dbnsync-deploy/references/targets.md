# Targets other than a plain Linux server

## NAS (Synology, QNAP, UGREEN)

- SSH has to be on (Synology: Control Panel → Terminal & SNMP; UGREEN UGOS: Control Panel → Terminal), and Docker installed from the app centre (Synology calls it Container Manager). The person does both; you can't reach the NAS until they have.
- Try `docker` without `sudo` first, since the account may be in the `docker` group. If it says permission denied, prefix `sudo`.
- Use a shared folder: `/volume1/docker/dbnsync` for the deployment and `/volume1/docker/dbnsync/s3` for the archive.
- UGOS's SFTP service doesn't see `/volume1`, so copy with the classic protocol, after making the folder over SSH: `scp -O docker-compose.yml Caddyfile user@nas:/volume1/docker/dbnsync/`, then `scp -O .env.example user@nas:/volume1/docker/dbnsync/.env`, then `chmod 600 .env` there at once.
- Ports 80 and 443 usually belong to the NAS's own page, which is why Caddy listens on 8443.
- Some NAS systems turn SSH off after a while, and UGOS has been seen to within hours. If `ssh` says *connection refused*, ask them to turn it back on; DbnSync keeps running regardless.

## Docker Desktop on Windows or macOS

Good for trying DbnSync, but a machine that sleeps pauses it, so say so.

- Skip the `chown` in step 3; Docker Desktop handles ownership.
- Set `S3_DATA_PATH=./s3`.
- In PowerShell use `curl.exe -fsSLO` in place of `curl -fsSLO`, and run the scripts from Git Bash or WSL.

## DigitalOcean

A Droplet runs the worker and Seq, and a Space is the S3 store. Both are billed by DigitalOcean (a Space is $5 a month for 250 GiB and 1 TiB of downloads, then $0.02 per GiB, and the archive grows about 750 GB a year with the defaults), so have the person create them, or get a clear yes first. Use the files in `Deploy/digitalocean`, not `Deploy/docker-compose.yml`.

1. They create a private Space, such as `dbn` in `nyc3`, and under API → Spaces Keys an access key that can read and write it.
2. They create a Droplet from the Docker image on Ubuntu; start with the 2 GB size.
3. Over SSH, make a folder and download `docker-compose.yml` and `.env.example` (as `.env`) from `Deploy/digitalocean`, then `chmod 600 .env`.
4. The person fills in `.env`: the Databento key, `SPACES_URL`, `SPACES_BUCKET`, `DBNSYNC_ACCESS_KEY`, `DBNSYNC_SECRET_KEY` and `SEQ_ADMIN_PASSWORD`. The keys are secrets, so the person types those in.
5. `docker compose pull`, then `docker compose up -d`.
6. Every port is bound to the Droplet. They reach the dashboard through a tunnel, `ssh -L 8080:localhost:8080 -L 5341:localhost:5341 root@droplet` then `http://localhost:8080`, or Tailscale.

To move an existing archive, stop the old worker first, then copy the bucket through the S3 API with a tool such as rclone. Never copy an S3 server's data folder.

DbnSpool, the optional tape worker, is off unless the person asks for it. It needs a second private Space, two more keys and the `DBNSPOOL_` lines of `.env`, and it starts with `--profile spool`.
