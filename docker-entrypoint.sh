#!/bin/sh
# Brings up the database, then the service - the whole of what this container does.
#
# The order is the point: Postgres is initialised, started and populated from /docker-entrypoint-initdb.d
# *before* the service is exec'd, so the service never sees an empty or half-filled database and needs no
# retry, no migration and no seeding code of its own. `set -e` means a failure anywhere here exits the
# container rather than leaving it serving an empty model, which is the one thing a test server must not
# do quietly.
set -eu

# Absolute, because `su` resets PATH: the ENV set in the Dockerfile applies to root, and the postgres
# shell started below would not find its own server binaries.
PGBIN=/usr/lib/postgresql/18/bin

PGDATA=/var/lib/postgresql/data
DB_USER=library
DB_NAME=library
DB_PASSWORD=library
export PGDATA

# Built fresh on every start rather than baked into the image: that is what makes a restart a reset, and
# it keeps the seed a single, well-known state that no earlier run can have modified.
rm -rf "$PGDATA"
mkdir -p "$PGDATA"
chown postgres:postgres "$PGDATA"
chmod 700 "$PGDATA"

# initdb reads the password from a file rather than an argument, so that it never appears in the process
# list. Removed immediately - the database listens on the loopback interface of this container only.
PWFILE=$(mktemp)
chmod 600 "$PWFILE"
printf '%s' "$DB_PASSWORD" > "$PWFILE"
chown postgres:postgres "$PWFILE"

# locale=C, so that ordering is plain byte order. `$orderby Title` has to answer identically wherever the
# container runs, and a locale-aware collation would sort the seed's umlauts by the host's idea of German.
echo "==> initialising database"
su postgres -c "$PGBIN/initdb --username=$DB_USER --pwfile=$PWFILE --encoding=UTF8 --locale=C" > /dev/null
rm -f "$PWFILE"

# Loopback only - the database is this container's business and nothing outside needs to reach it.
echo "==> starting postgres"
su postgres -c "$PGBIN/pg_ctl -D $PGDATA -o '-c listen_addresses=127.0.0.1 -p 5432' -w -t 60 start" > /dev/null

su postgres -c "$PGBIN/createdb --username=$DB_USER $DB_NAME"

# Name order, so 01-schema.sql precedes 02-seed.sql. ON_ERROR_STOP is what turns a broken seed into a
# failed start instead of a server quietly missing rows.
echo "==> applying schema and seed"
for script in /docker-entrypoint-initdb.d/*.sql; do
  [ -e "$script" ] || continue
  echo "    $(basename "$script")"
  su postgres -c "$PGBIN/psql --username=$DB_USER --dbname=$DB_NAME --set ON_ERROR_STOP=1 --quiet --file=$script"
done

echo "==> starting service"
ConnectionStrings__Library="Host=127.0.0.1;Port=5432;Database=$DB_NAME;Username=$DB_USER;Password=$DB_PASSWORD"
export ConnectionStrings__Library

# exec, so the service becomes PID 1 and `docker stop` reaches it rather than this script.
exec dotnet LibraryService.dll
