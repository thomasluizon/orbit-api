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
settled near 304 MB. This attributes about 50 MB to tokenizer and cost-estimation warmup, without
evidence of a growing tokenizer leak. The unbounded cache is a
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

## Measured cause and selected configuration

The reproduced failure mechanism is GC headroom: committed heap stays large
relative to live data, while the tokenizer and ORM/runtime baseline remain
resident. Production metrics establish the OOM and lack of headroom; the local
controlled comparison attributes the reproduced pressure to the GC policy.
Production retaining types remain unverified without a runtime heap capture.

[`evidence/ticket-2003-load.json`](evidence/ticket-2003-load.json) preserves every
sample and the runtime's effective GC configuration for the same 20-round
workload. The old settings failed the guard. The selected settings passed.

| Measurement | Old GC policy | Selected GC policy |
| --- | ---: | ---: |
| Steady working set maximum | 594,378,752 bytes | 384,745,472 bytes |
| Share of the 512 MiB budget | 110.7% | 71.7% |
| Managed bytes after diagnostic collection | 141,709,536 | 141,520,272 |
| Committed GC bytes after diagnostic collection | 259,883,008 | 115,605,504 |
| Cache entries at completion | 164 | 164 |

Comparable retained managed bytes and cache counts, with different committed
heap and working set, support a GC headroom fix. The selected run leaves
145.1 MiB of the instance budget free. No cache-size claim follows from
an entry count, and this run does not establish leak freedom over long uptime.
The instance plan and cost are unchanged.

## Runtime policy and guard

`Orbit.Api.csproj` publishes workstation GC, a 256 MiB heap hard limit, memory
conservation level 7, concurrent GC and tiered compilation. The settings travel
with the API image; they need no Render environment edit or plan change.
The explicit heap allowance leaves half of the container for native/runtime
memory and temporary work. Memory conservation makes the runtime collect more
frequently; the load guard must pass without requesting a collection during
its measured rounds.

The selected settings use the runtime's documented
[GC configuration contract](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector).
The default container heap allowance is 75%, which would leave 128 MiB for
everything outside the GC heap on this instance. That is a default derived from
the documented runtime policy, not an observed production heap counter.

`Orbit.Memory.Tests` is an isolated test host for the load, avoiding discovery
of the other test suites as part of the memory budget. It reuses the existing
Astra ranking workload and SQLite compatibility fixture. Seed data is prepared
once. Interceptors and encryption services are reused so the measurement does
not accumulate EF models created by test setup. No listener, application server,
container or external AI call is started.

The workload holds 150 slow event subscriptions for 50 accounts, with full
256-event buffers and 50 habit IDs per event. Across 20 rounds it reads Today
and the daily summary, changes 50 habits in each of four bulk actions, performs
an Astra ranking turn through the production handler and operation executor,
counts a captured SDK request with the production tokenizer, and streams an
export with 10,000 conversation rows to a discard stream. Both accepted results
and the working set limit are asserted. The first round warms the runtime;
every later round must remain below the target. Full collection happens only
after those samples, to distinguish retained memory from GC headroom.

```sh
dotnet build tests/Orbit.Memory.Tests
ORBIT_MEMORY_GUARD=1 DOTNET_gcServer=0 DOTNET_GCHeapHardLimit=10000000 DOTNET_GCConserveMemory=7 dotnet test tests/Orbit.Memory.Tests --no-build --no-restore --logger 'console;verbosity=detailed'
```

The environment heap limit is hexadecimal. The generated API runtime
configuration uses the equivalent decimal value, 268435456. Configuration tests
assert the published values. The API Tests workflow builds the isolated host
and runs this guard in a fresh process without coverage instrumentation.

The production `ProcessMemoryMonitor` logs its effective GC configuration on
startup and samples process memory every 30 seconds. At 80% it emits a warning
with working set, managed bytes, committed GC memory and the effective heap
limit. Warnings repeat at most every five minutes while high; recovery emits
one information message. It never collects the heap or changes request behavior.

## Release verification

The worker contract ends at the pull request. After the orchestrator releases
the API image, confirm `orbit-api` logs the 268435456-byte heap allowance and
workstation GC, then read Render's `orbit-api > Metrics > Memory` for a complete
24-hour window. Every instance's `memory_usage` must remain below 429496729
bytes; inspect `orbit-api > Events` for any `server_failed` event whose reason
contains `oomKilled`. Record that window and its maximum in the owning ticket.
The production acceptance criterion remains pending until this is complete.

Local macOS measurements with a GC allowance are not a Linux cgroup limit or a
production soak. The local host's CPU count, test runtime, SQLite provider and
native allocator differ from the deployed service. The read-only production
service metrics and the post-release observation are the release evidence.
