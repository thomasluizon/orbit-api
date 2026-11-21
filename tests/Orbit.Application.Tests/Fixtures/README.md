# Application test fixtures

Each fixture file holds the output of the AI model for one test case. The tests replay the file at `IAiIntentService`, so each run is deterministic and makes no network call.

## What a replay proves

- A replay proves that the application code handles the recorded output in the correct way.
- A replay proves nothing about the live model. A fixture is not a measure of model quality.
- A fixture is an authored or an edited record. It is not a provider capture, except the files in `TerminalReads/`. `TerminalReads/README.md` describes those files.

## Ids

- A fixture shows a redacted id where the source held a real id.
- The test rebinds each redacted id to an entity that the test owns.
- A fixture holds no account credential and no real user data.

## Provenance

- The `source` field of a JSON file names where the content came from.
- The `sha256` field of a JSON file holds the hash of that source content.

## Build

Add each fixture folder to `Orbit.Application.Tests.csproj` so that the build copies its JSON files to the output folder.
