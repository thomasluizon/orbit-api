# Distributed limiter PostgreSQL proof

This opt-in console runner checks the distributed rate limiter against a real PostgreSQL 17 database. It uses the real service, the production provider retries, independent contexts and synchronized starts. It starts no server, creates no database and applies no migration. `dotnet test` does not run it.

Never use a production database or a shared database. The limiter removes expired rows of the whole policy, so it can delete rows outside the prefix of the runner. Use an owned, disposable database with the current migrations applied.

Set these environment variables. Do not print their values.

- `ORBIT_RATE_LIMIT_PG17_CONNECTION_STRING`: the connection to the disposable database.
- `ORBIT_RATE_LIMIT_PG17_EXPECTED_DATABASE`: the exact database name. The runner checks it before it changes data.
- `ORBIT_RATE_LIMIT_PG17_DISPOSABLE=yes`: the explicit acknowledgement that the database is disposable.

```sh
dotnet run --project bench/Orbit.RateLimitProof
```

To compare two source trees on the same inputs, set `-p:OrbitApiRoot` to each tree. Rebuild between the two runs. Use the same database and the same connection settings.

```sh
dotnet run --project bench/Orbit.RateLimitProof -p:OrbitApiRoot="$RATE_LIMIT_BASELINE_REPO"
dotnet run --project bench/Orbit.RateLimitProof -p:OrbitApiRoot="$RATE_LIMIT_API_REPO"
```

## Pass criteria

- The exit code is 0.
- The output has the line `PG17_ASSERT failures=0; expected=0`.
- The output has the `PG17_IDENTITY` and `PG17_QUEUE` receipts from the real server.
