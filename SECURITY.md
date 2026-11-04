# Security

DbnSuite acts on real Databento® accounts and on archives that can be costly to
replace, so reports are welcome.

**To report a problem, use [Report a vulnerability](https://github.com/squideyes/DbnSuite/security/advisories/new)
on GitHub**, which keeps it private. Please don't open a public issue, and don't
include real keys or data. Say which tool and version, and how to see the problem.

This is an independent, free project, provided as is, so replies are on a
best-effort basis. Fixes go into the latest release; there are no older supported
versions.

Please report anything that could leak a key or a secret, spend money when spending
is off or beyond a limit, damage or delete an archive, or let someone in who should
not be. Problems in Databento's own services belong with Databento.

The DbnSync dashboard has no sign-in by design, and its ports should never be
opened to the internet. Reach it through Tailscale, an SSH tunnel or the Caddy
setup with a password, as the [docs](https://dbnsuite.squideyes.com/docs/dbnsync)
describe.

DbnSuite is an independent project with no official connection with Databento, Inc.
It is for data you are licensed to receive, and you use it at your own risk. See the
[notice](NOTICE.md).
