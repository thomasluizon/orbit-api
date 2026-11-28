# API memory budget

The production API has one Render Starter instance with a 512 MiB memory limit and
half a CPU. The kernel kills the container when the memory that it cannot reclaim
fills the limit. That memory is the anonymous memory plus the shmem of the process.
Clean file pages, for example mapped assemblies, stay in the working set, but the
kernel reclaims them first.

The warning line is 300 MiB (314,572,800 bytes) of anonymous plus shmem memory,
58.6% of the container. The remaining 212 MiB holds the file-backed code pages,
which measure about 176,000 kB after the load, and transient growth.
## Measured owners

[`evidence/api-memory-footprint.json`](evidence/api-memory-footprint.json) records the reads of this section. Each run uses these conditions:

- A native `linux/arm64` image in a container with `--memory=512m --memory-swap=512m --cpus=0.5`.
- A `postgres:17` database with TLS and no pending migration.
- The Production environment with placeholder secrets, and the production smoke login.
- The recorded HTTP load in [`api-memory-load.mjs`](../.github/scripts/api-memory-load.mjs).

The reads come from `/proc/1/status`, `/proc/1/smaps`, and the cgroup `memory.current` and `memory.stat`. Do not use a `linux/amd64` image for local reads. Rosetta adds a translation cache to its anonymous memory.

These are the owners of the memory 60 seconds after the recorded load:

| Owner | Resident | Kernel class |
| --- | ---: | --- |
| Mapped assemblies (`*.dll`), ReadyToRun | 151,028 kB | File, reclaimable |
| GC heap (committed) | 64,512 kB | Anonymous |
| Other unnamed memory (runtime, glibc arenas, thread stacks) | 39,360 kB | Anonymous |
| glibc main heap | 5,484 kB | Anonymous |
| `/memfd:doublemapper` data (loader heaps, stubs) | 60,420 kB | Shmem |
| `/memfd:doublemapper` code (JIT code) | 19,028 kB | Shmem |
| Native libraries (`*.so`) | 25,240 kB | File, reclaimable |

Anonymous plus shmem memory after the recorded load is 200.8 to 212.4 MB. A difference of less than about 12 MB is noise. These are the conclusions:

- ReadyToRun with TieredPGO off moves JIT code to file pages. It also stops the growth of JIT and loader memory with the variety of requests.
- The JIT settings and a lower `DOTNET_GCHeapHardLimit` do not lower the number. The heap limit is a ceiling.
- `MALLOC_ARENA_MAX=2` does not lower the number 60 seconds after the load. It lowers the glibc arena memory after an hour of uptime.
- Sentry and PostHog add about 25 MB together. Production needs both, so they stay.
- `DOTNET_EnableWriteXorExecute=0` and no `EnableRetryOnFailure` save memory, but neither is a fix. The first removes the W^X hardening of JIT memory. `VerifiedTransaction` depends on the retrying strategy.
- The working set after the recorded load is 366 to 398 MB. It is above the line because ReadyToRun puts about 150 MB of precompiled code in clean file pages that the kernel reclaims under pressure.

## Selected configuration

The image publishes the API ReadyToRun for the target architecture
(`-r linux-x64` or `-r linux-arm64`, `-p:PublishReadyToRun=true`, framework
dependent), and `Orbit.Api.csproj` sets `TieredPGO` to `false`. See
[ReadyToRun compilation](https://learn.microsoft.com/dotnet/core/deploying/ready-to-run)
and the [TieredPGO setting](https://learn.microsoft.com/dotnet/core/runtime-config/compilation#profile-guided-optimization).

The GC policy is workstation GC, a 256 MiB heap hard limit, memory conservation
level 7, concurrent GC and tiered compilation. It follows the runtime's
[GC configuration contract](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector).
The image sets `MALLOC_ARENA_MAX=2` in the `Dockerfile`, so glibc keeps two malloc
arenas instead of one for each thread that allocates natively. See the
[glibc memory allocation tunables](https://sourceware.org/glibc/manual/latest/html_node/Memory-Allocation-Tunables.html).
The production environment group sets no memory variable. The instance plan and
its cost do not change.

## Guards

`.github/scripts/api-memory-container.sh` runs in the `Docker migration bundle` job
on a push to `main`, on the image that the job builds. It does these steps:

1. It starts `postgres:17` with TLS and applies the migrations with the image's `efbundle` in a separate process.
2. It starts the API with `--memory=512m --memory-swap=512m --cpus=0.5`.
3. It reads memory at startup and after 60 seconds of rest.
4. It runs the recorded HTTP load and reads memory again, at once and after 60 seconds.

The guard fails when the image does not set `MALLOC_ARENA_MAX=2`, when the
container stops, when the kernel kills it, or when `RssAnon` plus `RssShmem`
reaches 314,572,800 bytes at any read. Set `ORBIT_MEMORY_PLATFORM=linux/arm64` to
run it natively on Apple silicon.

`Orbit.Memory.Tests` runs the in-process recorded load in a fresh test host. The
workload holds 150 slow event subscriptions for 50 accounts. Across 20 rounds, it
reads Today and the daily summary, and runs four bulk actions, an AI ranking
turn, a tokenizer count and an export of 10,000 conversation rows. Every round after
the first must stay below the same anonymous plus shmem line. The first token count
must retain less than 12 MiB of managed memory.

These unit tests keep the known owners of excess memory from coming back:

- `O200kTokenCounterTests` compares the counter with `TiktokenTokenizer` on the recorded production requests, fails when the vocabulary needs 8 MiB or more, and fails when `Orbit.Infrastructure` references `Microsoft.ML.Tokenizers`.
- `ValidatorRegistrationTests` fails when a validator without constructor dependencies is not a singleton, when 50 validations in new scopes compile a method, or when 50 constructions of the update validator compile a method.
- `DatabaseRegistrationTests` fails when a request scope reads the `Database` configuration.
- `StaleBadHabitPredicateTests` keeps `IsStaleBadHabit` equal to the query expression.

```sh
dotnet build tests/Orbit.Memory.Tests
ORBIT_MEMORY_GUARD=1 DOTNET_gcServer=0 DOTNET_GCHeapHardLimit=10000000 DOTNET_GCConserveMemory=7 dotnet test tests/Orbit.Memory.Tests --no-build --no-restore --logger 'console;verbosity=detailed'
bash .github/scripts/api-memory-container.sh <image tag>
```

The production `ProcessMemoryMonitor` logs its effective GC configuration on
startup. It reads `RssAnon` and `RssShmem` from `/proc/self/status` every 30
seconds. At the line, it emits a warning with anonymous, shmem, working set, managed
and committed GC memory, and the heap limit. Warnings repeat at most every five
minutes while the memory stays high. Recovery emits one information message. The
monitor never collects the heap or changes request behavior.

### Memory breakdown

Each budget warning is followed by one `API memory breakdown {MemoryBreakdown}`
warning. On startup, the monitor logs the same text once as
`API memory breakdown at startup {MemoryBreakdown}` at the information level. The
startup line is the baseline for growth with uptime. The text is compact JSON of at
most 4,000 characters, about 2,200 characters in a container. It has these fields:

| Field | Source | Content |
| --- | --- | --- |
| `rollupKb` | `/proc/self/smaps_rollup` | `Rss`, `Pss_Anon`, `Pss_File`, `Pss_Shmem`, clean and dirty pages, `Anonymous`, `AnonHugePages`, `Swap` |
| `kindsKb` | `/proc/self/smaps` | Mapping count, size, resident and anonymous kB for each kind |
| `largestKb` | `/proc/self/smaps` | The 12 largest resident mappings: kind, permissions, size, resident and anonymous kB |
| `threads` | `/proc/self/status` | The thread count of the process |
| `runtime` | Runtime | Thread pool threads and queue, timers, loaded and dynamic assemblies, JIT methods and IL bytes |
| `gc` | `GC.GetGCMemoryInfo` | Heap size, fragmentation, committed, promoted, pinned and finalization counts, collection counts, generation sizes |
| `gc.lastFull` | `GC.GetGCMemoryInfo` | Index, background flag, promoted bytes, heap size and fragmentation of the last full GC |

The kinds are `anonymous`, `heap`, `stack`, `doublemapper-code`,
`doublemapper-data`, `shm`, `dll`, `so`, `file` and `kernel`. A `doublemapper`
mapping with execute permission holds JIT code and stubs. The other `doublemapper`
mappings hold loader heaps and writable views. The text holds kind names and
numbers only. It never holds a mapping path, so it cannot show a file name or a
secret. A `/proc/self` file that the kernel refuses is named in `unavailable`.

The monitor reads the breakdown at most once per warning interval, and only when
the warning is enabled. One read streams about 35,000 lines of `/proc/self/smaps`.

Use these rules to read a warning:

- Compare `gc.heapSize` with `gc.lastFull.promoted`. The difference is managed memory that a full GC can return.
- Compare `kindsKb` with the startup line of the same instance. The kind whose `rss` grows is the owner of the growth.
- If `doublemapper-code` grows, JIT code grows. If `doublemapper-data` grows, loader data grows, for example from new types or dynamic code.
- If `anonymous` grows and `gc.committed` does not, native memory grows outside the GC heap.

The kernel reports `RssAnon` and `RssShmem` since Linux 4.5, and a container reads
them from the host kernel. If a field is missing or malformed, the monitor does not
stop. It emits one warning that names the field and then compares the whole working
set with the line. The working set is larger than anonymous plus shmem memory, so
this fallback can warn early, but it cannot hide memory pressure.
