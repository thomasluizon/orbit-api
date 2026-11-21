# Minimum supported version

The API rejects a client build that is older than the minimum supported version. The API answers that client with HTTP 426.

## Live floor

The live floor is the `Value` of the `MinSupportedVersion` row in the production `AppConfigs` table. Read the floor before you plan a raise. Read it again after each write.

```sql
SELECT "Key", "Value"
FROM "AppConfigs"
WHERE "Key" = 'MinSupportedVersion';
```

## Preconditions

Record each precondition in the owning ticket before you raise the floor.

1. Read the active version distribution and the release dates in the Google Play Console.
2. Confirm that the proposed floor is already live. The build must send `X-App-Version` and must handle HTTP 426 with an upgrade prompt.
3. Check `APP_VERSION` in the Render services `orbit-web` and `orbit-web-staging`. An unset value fails open. A set value must be at least the proposed floor.
4. Confirm that the build that carries the proposed floor is live in the fleet.

## Raise the floor

Use a guarded update. The update changes the row only when the row still holds the floor that you read.

```sql
UPDATE "AppConfigs"
SET "Value" = '<new floor>'
WHERE "Key" = 'MinSupportedVersion'
  AND "Value" = '<current floor>';
```

Read the row back and verify the exact value. Monitor the Android and web HTTP 426 traffic and the client reports for 24 hours.

## Cache

`AppConfigService` caches the value for 30 minutes in each process. A change takes up to 30 minutes to affect a warm process. Running instances can start to enforce the floor at different times. The database write is not an immediate activation of the whole fleet.

## Rollback

A rollback does not need a deploy. Set the row to `0.0.0`, read it back, and wait for the same cache window.

```sql
UPDATE "AppConfigs"
SET "Value" = '0.0.0'
WHERE "Key" = 'MinSupportedVersion';
```
