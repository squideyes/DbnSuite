#!/bin/sh
# Checks a DbnSync .env and reports on each setting without printing any value.
# Exits 1 on FAIL and 2 when spending is switched on.
set -u

file=${1:-.env}
[ -f "$file" ] || { echo "FAIL $file not found"; exit 1; }

failed=0
paid=0

value() { sed -n "s/^$1=//p" "$file" | tail -n 1 | tr -d '\r'; }

ok() { echo "ok   $1"; }

fail() { echo "FAIL $1"; failed=1; }

need() { if [ -n "$(value "$1")" ]; then ok "$1 is set"; else fail "$1 is empty"; fi; }

case $(value DATABENTO_API_KEY) in
    db-?*) ok "DATABENTO_API_KEY looks like a Databento key" ;;
    "") fail "DATABENTO_API_KEY is empty" ;;
    *) fail "DATABENTO_API_KEY does not start db-" ;;
esac

need SEQ_ADMIN_PASSWORD

# A DigitalOcean install keeps the archive in Spaces: it has no RustFS, so none of its settings.
store=$(value S3_SERVICE_URL)
secret=$(value S3_ROOT_SECRET_KEY)
if [ -n "$(value SPACES_URL)" ]; then
    ok "SPACES_URL is set, so the archive is in DigitalOcean Spaces"
    need DBNSYNC_ACCESS_KEY
    need DBNSYNC_SECRET_KEY
else
    need S3_ROOT_ACCESS_KEY
    need S3_DATA_PATH

    if [ -n "$store" ] && [ "$store" != "http://s3:9000" ]; then
        need DBNSYNC_ACCESS_KEY
        need DBNSYNC_SECRET_KEY
    elif [ "${#secret}" -lt 24 ]; then
        fail "S3_ROOT_SECRET_KEY is shorter than 24 characters"
    else
        ok "S3_ROOT_SECRET_KEY is long enough"
    fi
fi

[ -n "$(value DBNSYNC_LOGS_URL)" ] || echo "note DBNSYNC_LOGS_URL is empty, so the dashboard hides its log links"

logs=$(value DBNSYNC_LOGS_URL)
seq=$(value SEQ_PORT)
seq=${seq:-5341}
case $logs in
    ""|*":$seq"|*":$seq/"*) ;;
    *) echo "note DBNSYNC_LOGS_URL does not use SEQ_PORT ($seq), so its links may open another Seq" ;;
esac

# Two installs on one machine must stay apart. A second RustFS on a live archive
# folder is the worst mistake open here, and nothing else stops it.
dir=$(cd "$(dirname "$file")" && pwd)
project=$(value COMPOSE_PROJECT_NAME)
project=${project:-dbnsync}
path=$(value S3_DATA_PATH)
case $path in ./*) path="$dir/${path#./}" ;; esac

if ids=$(docker ps -q 2>/dev/null); then
    clash=0
    format='{{index .Config.Labels "com.docker.compose.project"}}|{{index .Config.Labels "com.docker.compose.project.working_dir"}}|{{range .Mounts}}{{.Source}};{{end}}'

    while IFS='|' read -r name home mounts; do
        [ -n "$name$home$mounts" ] || continue
        [ "$home" != "$dir" ] || continue

        if [ -n "$path" ]; then
            case ";$mounts" in
                *";$path;"*) fail "S3_DATA_PATH is already in use by the install in $home"; clash=1 ;;
            esac
        fi

        if [ "$name" = "$project" ] && [ "$clash" -eq 0 ]; then
            fail "project name $project belongs to the install in $home: set COMPOSE_PROJECT_NAME"
            clash=1
        fi
    done <<EOF
$([ -z "$ids" ] || docker inspect -f "$format" $ids 2>/dev/null)
EOF

    [ "$clash" -ne 0 ] || ok "no other install here uses this archive folder or project name"
else
    echo "note docker is not reachable, so another install on this machine could not be checked for"
fi

case $(ls -l "$file") in
    -??-------*) ok "$file is private to its owner" ;;
    *) echo "note $file is readable by others: chmod 600 $file" ;;
esac

if [ "$(value DBNSYNC_ALLOW_PAID_JOBS)" = "true" ]; then
    echo "PAID DBNSYNC_ALLOW_PAID_JOBS is true: jobs may be billed to the Databento account"
    paid=1
else
    ok "spending is off: only jobs quoted at \$0 go"
fi

[ "$failed" -eq 0 ] || exit 1
[ "$paid" -eq 0 ] || exit 2
