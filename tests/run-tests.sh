#!/usr/bin/env bash
# Builds AcxiomCRM, starts it on a FRESH test database (tests/test.db, port 5099; your app.db is
# never touched), runs the HTTP acceptance tests and the headless-Edge browser tests, then stops it.
# Requires: .NET SDK, Python 3 (sqlite checks), Node 22+ and Microsoft Edge (browser tests),
# and the development seed passwords in user-secrets (see README).
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
PORT=5099
BASE="http://localhost:$PORT"
DB="tests/test.db"

dotnet build -nologo -v q || { echo "Build failed (if the exe is locked, stop the running dev server first)"; exit 1; }
rm -f "$DB" "$DB-shm" "$DB-wal" tests/*.txt

ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__DefaultConnection="DataSource=$DB;Cache=Shared" \
"$ROOT/bin/Debug/net10.0/AcxiomCRM.exe" --urls "$BASE" > tests/app-test.log 2>&1 &
APP=$!   # only this test instance is stopped afterwards; a dev server on :5058 is left alone
trap 'kill $APP 2>/dev/null' EXIT

for i in $(seq 1 60); do curl -s -o /dev/null "$BASE/Identity/Account/Login" && break; sleep 1; done

BASE="$BASE" DB="$ROOT/$DB" bash tests/acceptance.sh; HTTP=$?
echo
node tests/browser-tests.mjs "$BASE"; BROWSER=$?

kill $APP 2>/dev/null
echo
echo "HTTP tests exit=$HTTP, browser tests exit=$BROWSER"
[ $HTTP = 0 ] && [ $BROWSER = 0 ]
