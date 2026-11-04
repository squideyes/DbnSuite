# Troubleshooting

Start with what the service says about itself, before changing anything:

```sh
docker compose ps
docker compose logs --tail 100 dbnsync
curl -s http://localhost:8080/health
curl -s http://localhost:8080/status
scripts/check-env.sh .env
```

Use `DBNSYNC_PORT` in place of 8080 if it is set.

The dashboard's Activity page says why jobs failed, and each issue on it explains what it means and whether it needs anyone.

| Symptom | Cause and fix |
|---|---|
| `s3` keeps restarting, or logs *permission denied* | The archive folder isn't owned by user 10001. Look at the folder, then `sudo chown -R 10001:10001` on it and `docker compose up -d`. |
| `ssh` says *connection refused* | The NAS has turned SSH off. The service is unaffected: ask them to turn it back on in its Control Panel. |
| `set DATABENTO_API_KEY in .env` on start | A required setting is empty, or `.env` isn't beside `docker-compose.yml`. |
| Every job is refused as over the spend limit | The key's plan doesn't include that data at $0. Check the plan, `DbnSync__HistoryStart`, `DbnSync__ReachBackTo` and the schemas switched on. Don't raise a limit. |
| The worker won't start: *Spend limits must nest* or *must all be set* | The spend policy is half set. Tell them; it is theirs to set in full or clear. |
| The worker won't start on a dataset | Only datasets with a profile are supported, and contributed profiles need `DbnSync__AllowContributedProfile`. Explain the risk and let them choose. |
| The Activity page shows *Databento had a server error* | A 5xx from Databento's side. It tries again on the next pass, and a paid job it was submitting stays counted. Nothing to fix unless it lasts for hours; then check Databento's status page. |
| A paid job stops going, with others still going | The spend ledger shows an earlier purchase of it, and it couldn't get or read Databento's list of jobs to see whether that went through, so it won't buy this job until it can. The issue on the Activity page says why. Don't change the spend policy or the ledger to get round it. |
| The dashboard shows Lost days | The worker was down, or couldn't reach Databento, for longer than a window. The Activity page says why. |
| A reader's key is refused | Check the user has `dbn-readonly` attached and the bucket name matches the policy. |
| The ETA reads Unknown | Nothing has been stored in the last 24 hours. The Activity page shows what is in the way. |

Still stuck: have them open an issue at https://github.com/squideyes/DbnSuite/issues with the issue code from the dashboard, and check that what they paste holds no keys.
