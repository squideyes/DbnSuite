#!/bin/sh
# Generates S3_ROOT_SECRET_KEY and SEQ_ADMIN_PASSWORD into an empty slot in .env.
# A value already there is kept, and no value is ever printed.
set -eu

file=${1:-.env}
[ -f "$file" ] || { echo "FAIL $file not found"; exit 1; }
command -v openssl >/dev/null || { echo "FAIL openssl not found"; exit 1; }

fill() {
    if grep -q "^$1=..*" "$file"; then
        echo "kept      $1"
        return
    fi

    secret=$(openssl rand -hex 24)
    awk -v key="$1" -v value="$secret" '
        { sub(/\r$/, "") }
        $0 == key "=" { print key "=" value; done = 1; next }
        { print }
        END { if (!done) print key "=" value }
    ' "$file" > "$file.tmp"
    cat "$file.tmp" > "$file"
    rm -f "$file.tmp"
    echo "generated $1"
}

fill S3_ROOT_SECRET_KEY
fill SEQ_ADMIN_PASSWORD
chmod 600 "$file"
