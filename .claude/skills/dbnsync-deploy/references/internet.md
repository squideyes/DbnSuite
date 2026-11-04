# Reaching it from the internet

Readers on the same network use `http://<server>:9000`. Recommend the first option below unless they need to serve people whose devices they don't control.

## Only their own devices: Tailscale

They install Tailscale on the server and each device, then use `http://<server's Tailscale name>:9000` from anywhere. Traffic is encrypted, no router port opens, and no domain or certificate is needed.

## Anyone with a key: HTTPS on their own domain

Caddy fronts the S3 API and the dashboard, each under its own name, and renews Let's Encrypt certificates itself. Tell them first that the S3 API is then reachable by anyone who finds it, and only the keys keep them out: long generated secrets, read-only keys for readers, and never the administrator's key from outside. Get a clear yes before going on.

1. They give the server a fixed address on the router (a DHCP reservation).
2. They point two DNS names at their public IP with `A` records, such as `s3.example.com` and `dbnsync.example.com`. Both are needed: Caddy won't start with only one.
3. They forward TCP 443 on the router to the server's port 8443, and nothing else.
4. Have them make the dashboard's password hash, typing the password into their own terminal so that it stays out of this conversation:

   ```sh
   docker run --rm caddy:2.10.2 caddy hash-password --plaintext 'the password' | base64 -w0
   ```

5. In `.env`, set `S3_DOMAIN`, `DASHBOARD_DOMAIN` and `DASHBOARD_USER` yourself; they paste the hash after `DASHBOARD_PASSWORD_HASH=`.
6. Start Caddy with the rest: `docker compose --profile internet up -d`.
7. Check `https://<dashboard name>` asks for the password, and that `https://<s3 name>` answers.
