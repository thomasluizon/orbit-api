# API memory budget

The production API has one Render Starter instance with a 512 MiB memory limit.
The working set target is below 80% (429,496,729 bytes), which leaves at least
102.4 MiB for transient allocations and native growth.

## Incident evidence

[`evidence/ticket-2003-memory.json`](evidence/ticket-2003-memory.json) records the
read-only production measurements for thomasluizon/orbit-tickets#2003. The old
instance ranged from 495,439,870 to 533,102,600 bytes during the incident day,
against a reported 536,870,900 byte limit. These are service measurements, not
managed heap measurements.

After the GC policy below was released, the `ProcessMemoryMonitor` warnings in
production showed a working set of 544 to 547 MB, with 118 to 144 MB managed and
160 to 168 MB of committed GC heap. One minute after the next restart, the working
set was 455,282,688 bytes with 119,447,552 bytes of committed GC heap. About
380 MB of the working set was outside the GC heap.

## Native memory attribution

[`evidence/ticket-2003-native-memory.json`](evidence/ticket-2003-native-memory.json)
records every read. The API image ran in a Docker container with
`--memory=512m --memory-swap=512m`, against a `postgres:17` container, with the
Production environment and placeholder secrets. Each run recorded `/proc/1/status`,
`/proc/1/smaps`, the cgroup `memory.current` and `memory.stat`, and the
`ProcessMemoryMonitor` log lines at start and after one minute. The attribution
groups `/proc/1/smaps` by mapping. The native reads use `linux/arm64`, because
Rosetta adds its own translation cache to the anonymous memory of a `linux/amd64`
process on this host.

These are the owners of the memory outside the GC heap, at one minute after a start
on a migrated database, before the change:

| Owner | Resident | Kernel class |
| --- | ---: | --- |
| Mapped assemblies (`*.dll`) | 96.6 MB | Clean file pages, reclaimable |
| `/memfd:doublemapper` writable views (loader heaps, stubs) | 52.2 MB | Shmem, not reclaimable without swap |
| `/memfd:doublemapper` executable views (JIT code) | 17.7 MB | Shmem, not reclaimable without swap |
| Native libraries (`*.so`) | 23.6 MB | Clean file pages, reclaimable |
| glibc malloc arena heaps | 17.1 MB | Anonymous |
| glibc main heap | 6.0 MB | Anonymous |

The `doublemapper` memfd is the runtime's write xor execute mapping of
executable memory. Under the recorded load, the same owner held 86 MB of shmem.
With `DOTNET_EnableWriteXorExecute=0` the same pages moved from shmem to anonymous
memory, and the total did not change. Thus the owner is the volume of JIT code and
loader data, not the double mapping.

These candidates were measured and ruled out as the main owner:

- `Microsoft.CodeAnalysis` assemblies: `/proc/1/maps` has no mapping for them. The files ship in `/app`, but the runtime never loads them.
- `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/efbundle`: production has no start or pre-deploy command, so `efbundle` never runs in the API process.
- glibc malloc arenas: 45 arena heaps held 16 to 24 MB at idle and 45 MB at the recorded load peak. `MALLOC_ARENA_MAX=2` saved 7 MB under load, which does not prove a cause.
- Sentry: no profiling integration or EventPipe session is configured.

## Startup migrations and the GC heap

When the API applies every migration to an empty database at startup, the gen2
generation holds 150,789,648 bytes after the last collection, but a heap dump finds
only 23,798,187 live bytes. The migration work promotes short-lived objects to
gen2, and an idle process does not reach the gen2 budget that frees them. In that
state the arm64 working set reached 430,088 kB. The `linux/amd64` image was
OOM-killed during that startup, after its monitor reported a 597,721,088 byte
working set. On a migrated database the committed GC heap stays near 33 MB. A
release normally applies few migrations, so this path is not the production steady
state.

## Selected configuration

The image publishes the API ReadyToRun for the target architecture
(`-r linux-x64` or `-r linux-arm64`, `-p:PublishReadyToRun=true`, framework
dependent), and `Orbit.Api.csproj` sets `TieredPGO` to `false`. Precompiled code
then lives in the mapped assembly, which is a clean file page that the kernel can
reclaim. Hot methods rejit once to tier 1 and keep no instrumented copy. See
[ReadyToRun compilation](https://learn.microsoft.com/dotnet/core/deploying/ready-to-run)
and the [TieredPGO setting](https://learn.microsoft.com/dotnet/core/runtime-config/compilation#profile-guided-optimization).

| Measurement (arm64, 512 MiB container) | Before | After |
| --- | ---: | ---: |
| Recorded load peak, `doublemapper` shmem | 86,352 kB | 43,088 kB |
| Recorded load peak, cgroup `memory.current` | 341,102,592 B | 307,568,640 B |
| Recorded load peak, working set | 366,236 kB | 360,288 kB |
| Migrated start + 1 minute, shmem | 69,932 kB | 45,400 kB |
| Migrated start + 1 minute, working set | 249,568 kB | 264,280 kB |
| Fresh database start + 1 minute, shmem | 94,196 kB | 49,828 kB |
| Fresh database start + 1 minute, cgroup `memory.current` | 331,689,984 B | 280,199,168 B |

The memory that cannot be reclaimed falls by about 25 to 44 MB. The working set can
rise, because it counts the larger clean ReadyToRun pages. The recorded load in
`tests/Orbit.Memory.Tests` stays below the warning line before and after the change,
so it does not reproduce the production surplus. The local runs had no inbound HTTP
traffic, no TLS database connection and no Sentry DSN. The about 100 MB of
production memory outside the GC heap that these runs do not show remains
unattributed.

The GC policy from the earlier change stays: workstation GC, a 256 MiB heap hard
limit, memory conservation level 7, concurrent GC and tiered compilation. They
follow the runtime's
[GC configuration contract](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector).
The instance plan and its cost do not change.

## Guards

`.github/scripts/api-memory-container.sh` runs in the `Docker migration bundle` job
on the image that the job builds. It applies the migrations with the image's
`efbundle` in a separate process, starts the API in a `linux/amd64` container with
`--memory=512m --memory-swap=512m`, and waits 60 seconds after Hangfire starts. It
prints the `/proc` and cgroup reads and fails when the container stops, when the
kernel kills it, or when `VmRSS` reaches 429,496,729 bytes.

`Orbit.Memory.Tests` runs the recorded load in a fresh test host. The workload holds
150 slow event subscriptions for 50 accounts, and across 20 rounds it reads Today and
the daily summary, runs four bulk actions, an Astra ranking turn, a tokenizer count
and an export of 10,000 conversation rows. Every round after the first must stay
below the target.

```sh
dotnet build tests/Orbit.Memory.Tests
ORBIT_MEMORY_GUARD=1 DOTNET_gcServer=0 DOTNET_GCHeapHardLimit=10000000 DOTNET_GCConserveMemory=7 dotnet test tests/Orbit.Memory.Tests --no-build --no-restore --logger 'console;verbosity=detailed'
bash .github/scripts/api-memory-container.sh <linux/amd64 image tag>
```

The production `ProcessMemoryMonitor` logs its effective GC configuration on
startup and samples process memory every 30 seconds. At 80% it emits a warning
with working set, managed bytes, committed GC memory and the effective heap
limit. Warnings repeat at most every five minutes while high; recovery emits
one information message. It never collects the heap or changes request behavior.

## Release verification

After the orchestrator releases the API image, read Render's
`orbit-api > Metrics > Memory` for a complete 24-hour window. Every instance's
`memory_usage` must remain below 429496729 bytes. Inspect `orbit-api > Events` for
any `server_failed` event whose reason contains `oomKilled`, and search the logs for
`API memory budget exceeded`. Record that window and its maximum in the owning
ticket. The production acceptance criterion remains open until this is complete.
