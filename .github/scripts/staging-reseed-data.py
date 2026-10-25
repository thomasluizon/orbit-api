import json
import os
from pathlib import Path
import subprocess
import sys
from urllib.parse import parse_qs, unquote, urlsplit


PSQL = ["psql", "--no-psqlrc", "--no-password", "--quiet", "--tuples-only", "--no-align", "--set=ON_ERROR_STOP=1"]
COUNTS_SQL = r"""
SELECT format('SELECT json_build_array(%L, %L, count(*)) FROM %I.%I;',
              schemaname, tablename, schemaname, tablename)
FROM pg_catalog.pg_tables
WHERE schemaname <> 'information_schema'
  AND schemaname !~ '^pg_'
ORDER BY schemaname, tablename
\gexec
"""


def require_success(status, operation):
    if status != 0:
        sys.exit(f"Staging data {operation} failed (exit {status})")


def read_counts(output):
    rows = [json.loads(line) for line in output.splitlines() if line.strip()]
    if not rows:
        sys.exit("Staging data count capture returned no tables")
    counts = {}
    for row in rows:
        if (not isinstance(row, list) or len(row) != 3
                or not all(isinstance(name, str) for name in row[:2])
                or type(row[2]) is not int or row[2] < 0):
            sys.exit("Staging data count capture returned an invalid row")
        key = tuple(row[:2])
        if key in counts:
            sys.exit("Staging data count capture returned a duplicate table")
        counts[key] = row[2]
    return counts


def connection_environment():
    environment = os.environ.copy()
    try:
        connection = urlsplit(environment.pop("STAGING_DATABASE_URL"))
        if connection.scheme not in ("postgres", "postgresql"):
            raise ValueError
        host = connection.hostname
        port = connection.port or 5432
        user = unquote(connection.username or "")
        password = unquote(connection.password or "")
        database = unquote(connection.path.removeprefix("/"))
        if not all((host, user, password, database)):
            raise ValueError
        sslmode = parse_qs(connection.query).get("sslmode", ["require"])[-1]
    except (KeyError, ValueError):
        sys.exit("Staging data connection URL is missing or invalid")
    environment.update(PGHOST=host, PGPORT=str(port), PGUSER=user, PGPASSWORD=password,
                       PGDATABASE=database, PGSSLMODE=sslmode, PGCONNECT_TIMEOUT="30")
    return environment


def backup(directory, environment):
    ready = directory / "ready"
    ready.unlink(missing_ok=True)
    with subprocess.Popen(PSQL, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True,
                          env=environment) as session:
        try:
            session.stdin.write("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\nSELECT pg_export_snapshot();\n")
            session.stdin.flush()
            snapshot = session.stdout.readline().strip()
            if not snapshot:
                sys.exit("Staging data snapshot export failed")
            result = subprocess.run([
                "pg_dump", "--format=custom", "--no-owner", "--no-acl", "--no-password",
                f"--snapshot={snapshot}", f"--file={directory / 'database.dump'}",
            ], env=environment)
            require_success(result.returncode, "dump")
            output, _ = session.communicate(COUNTS_SQL + "\nCOMMIT;\n")
            require_success(session.returncode, "source counts")
        finally:
            if session.poll() is None:
                session.terminate()
    counts = read_counts(output)
    require_success(subprocess.run([
        "pg_restore", "--list", str(directory / "database.dump"),
    ], stdout=subprocess.DEVNULL, env=environment).returncode, "archive validation")
    (directory / "counts.jsonl").write_text(output)
    ready.write_text("complete\n")
    print(f"Captured staging data and counts for {len(counts)} tables")


def restore(directory, environment):
    if not (directory / "ready").is_file():
        sys.exit("Staging data backup is incomplete")
    expected = read_counts((directory / "counts.jsonl").read_text())
    require_success(subprocess.run([
        "pg_restore", "--exit-on-error", "--single-transaction", "--no-owner", "--no-acl",
        "--no-password", "--dbname", environment["PGDATABASE"], str(directory / "database.dump"),
    ], env=environment).returncode, "restore")
    result = subprocess.run(PSQL, input="BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\n"
                            + COUNTS_SQL + "\nCOMMIT;\n", stdout=subprocess.PIPE, text=True,
                            env=environment)
    require_success(result.returncode, "destination counts")
    actual = read_counts(result.stdout)
    if expected != actual:
        for table in sorted(expected.keys() | actual.keys()):
            if expected.get(table) != actual.get(table):
                print(f"Staging table {json.dumps(table)}: source={expected.get(table)}, "
                      f"replacement={actual.get(table)}", file=sys.stderr)
        sys.exit("Staging data row counts do not match")
    print(f"Verified staging data row counts for all {len(expected)} tables")


if __name__ == "__main__":
    os.umask(0o077)
    directory = Path(os.environ["STAGING_CARRY_DIRECTORY"])
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    environment = connection_environment()
    if sys.argv[1] == "backup":
        backup(directory, environment)
    elif sys.argv[1] == "restore":
        restore(directory, environment)
    else:
        sys.exit("Unsupported staging data operation")
