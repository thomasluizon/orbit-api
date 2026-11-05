# API memory budget

The production API has one Render Starter instance with a 512 MiB memory limit.
The working set target is below 80% (429,496,729 bytes), leaving at least 102.4 MiB
for transient allocations and native growth.

## Incident evidence

[`evidence/ticket-2003-memory.json`](evidence/ticket-2003-memory.json) records the
read-only production measurements for thomasluizon/orbit-tickets#2003. The old
instance ranged from 495,439,870 to 533,102,600 bytes during the incident day,
against a reported 536,870,900 byte limit. The upper sample consumed 99.3% of
the limit. These are service measurements, not managed heap measurements.

The deployed image was inspected through the GHCR manifest and layer archive,
without starting a container. Its `Orbit.Api.runtimeconfig.json` explicitly
selected Server GC and supplied no heap limit. The image environment, direct
Render service environment and linked `orbit-production-api` environment group
had no GC overrides. Concurrent GC and tiered compilation were unspecified.
Production SSH rejected the available local key, so there is no production
heap snapshot and no claim that a specific managed type caused the kill.

The local tokenizer probe runs the real `AiRequestCostEstimator` and its
`o200k_base` tokenizer. In an isolated .NET 10.0.12 macOS ARM64 test host,
managed memory increased from 158,863,064 to 208,998,664 bytes after the first
200 distinct requests and a diagnostic full collection. Two more batches
retained 209,179,032 and 209,331,568 bytes, with 601 cached counts. Working set
settled near 304 MB. This identifies a persistent tokenizer baseline of about
50 MB, without evidence of a growing tokenizer leak. The unbounded cache is a
separate retention risk; a count of cached entries is not a byte measurement.

## Reproduce the measurements

Run the existing query, bulk, streaming export, chat and event tests before
changing their behavior:

```sh
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~UserCacheVersionTests|FullyQualifiedName~AccountEventBusScaleTests|FullyQualifiedName~StreamingExportTests|FullyQualifiedName~BulkHabitRoundTripTests|FullyQualifiedName~ChatContextScaleTests'
dotnet test tests/Orbit.Infrastructure.Tests --filter FullyQualifiedName~TokenizerMemoryProbeTests --logger 'console;verbosity=detailed'
```

The unchanged baseline suite passed all 17 cases. The tokenizer probe passed
and emits its working set, managed bytes, committed heap, available GC memory
and GC flavor at every phase. Its full collections are diagnostic only and
are never performed by the production API.

To re-read the Render evidence, use authenticated GET requests to
`/v1/metrics/memory` and `/v1/metrics/memory-limit`, with `resource` equal to the
service ID and `resolutionSeconds=1800`. Use the incident window in the evidence
artifact for `startTime` and `endTime`. Each actual response is an array containing
`labels`, `unit`, and `values`, where a value contains `timestamp` and `value`.
Service configuration came from `render services --output json`,
`GET /v1/services/{serviceId}/env-vars`, `GET /v1/env-groups` and
`GET /v1/env-groups/{envGroupId}`. The linked group's actual response exposes
`envVars` with `key` and `value`; only the absence of GC keys is recorded.

The GHCR manifest response exposes `config.digest` and `layers[].digest`.
Read the configuration blob for the image environment and read the application
layer tar stream for `app/Orbit.Api.runtimeconfig.json`. Authentication tokens
and unrelated environment values must stay out of evidence artifacts.
