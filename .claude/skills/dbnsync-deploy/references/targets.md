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

## DigitalOcean, without a NAS

A Droplet runs the worker and Seq, and a Space replaces RustFS. Both are billed by DigitalOcean (a Space is $5 a month for 250 GB, then $0.02 per GB), so have the person create them, or get a clear yes first.

1. They create a Space, such as `dbn` in `nyc3`, and under API → Spaces Keys an access key for it.
2. They create a Droplet from the Docker image on Ubuntu; start with the 2 GB size. Do steps 1 and 2 of the skill over SSH.
3. Skip step 3. In `.env` set the lines below. The key and its secret are secrets, so the person types those in. The `unused` values are required by the Compose file.

   ```
   S3_SERVICE_URL=https://nyc3.digitaloceanspaces.com
   S3_BUCKET=dbn
   DBNSYNC_ACCESS_KEY=
   DBNSYNC_SECRET_KEY=
   S3_ROOT_ACCESS_KEY=unused
   S3_ROOT_SECRET_KEY=unused
   S3_DATA_PATH=/tmp/unused
   ```

4. Start only the worker and Seq:

   ```sh
   docker compose pull dbnsync seq
   docker compose up -d --no-deps dbnsync seq
   ```

5. Don't open port 8080. They reach the dashboard through a tunnel, `ssh -L 8080:localhost:8080 root@droplet` then `http://localhost:8080`, or Tailscale.
