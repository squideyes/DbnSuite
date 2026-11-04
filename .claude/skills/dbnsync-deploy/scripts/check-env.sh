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

need S3_ROOT_ACCESS_KEY
need S3_DATA_PATH
need SEQ_ADMIN_PASSWORD

store=$(value S3_SERVICE_URL)
secret=$(value S3_ROOT_SECRET_KEY)
if [ -n "$store" ] && [ "$store" != "http://s3:9000" ]; then
    need DBNSYNC_ACCESS_KEY
    need DBNSYNC_SECRET_KEY
elif [ "${#secret}" -lt 24 ]; then
    fail "S3_ROOT_SECRET_KEY is shorter than 24 characters"
else
    ok "S3_ROOT_SECRET_KEY is long enough"
fi

[ -n "$(value DBNSYNC_LOGS_URL)" ] || echo "note DBNSYNC_LOGS_URL is empty, so the dashboard hides its log links"

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
