# API memory budget

The production API has one Render Starter instance with a 512 MiB memory limit and
half a CPU. The kernel kills the container when the memory that it cannot reclaim
fills the limit. That memory is the anonymous memory plus the shmem of the process.
Clean file pages, for example mapped assemblies, stay in the working set, but the
kernel reclaims them first.

The warning line is 300 MiB (314,572,800 bytes) of anonymous plus shmem memory,
58.6% of the container. The remaining 212 MiB holds the file-backed code pages,
which measure about 176,000 kB after the load, and transient growth.

## Production evidence

[`evidence/ticket-2003-memory.json`](evidence/ticket-2003-memory.json) records the
read-only Render measurements of the first out-of-memory kill. Production runs
`f84ab32cf`, which has JIT compilation without ReadyToRun and with TieredPGO. Around
the second kill, its `ProcessMemoryMonitor` warnings before and after the restart show these values:

| Read | Value |
| --- | ---: |
| Working set | 455 to 547 MB |
| Committed GC heap | 119 to 170 MB |
| Managed bytes | 91 to 144 MB |

Thus 300 to 400 MB of the working set is outside the GC heap.

## Reproduction

[`evidence/ticket-2003-starter-footprint.json`](evidence/ticket-2003-starter-footprint.json)
records every read in this section. Each run uses these conditions:

- A native `linux/arm64` image in a container with `--memory=512m --memory-swap=512m --cpus=0.5`.
- A `postgres:17` database with TLS and no pending migration.
- The Production environment with placeholder secrets, and the production smoke login.
- The recorded HTTP load in [`api-memory-load.mjs`](../.github/scripts/api-memory-load.mjs).

The recorded HTTP load logs in, creates 100 habits, and holds three event streams.
In each of 30 rounds, eight concurrent clients read Today, the month, the profile,
a habit detail and its metrics. They also log ten habits and skip ten habits in
bulk. Every tenth round exports the account. The reads come from `/proc/1/status`,
`/proc/1/smaps`, and the cgroup `memory.current` and `memory.stat`. A
`linux/amd64` image is not used for local reads, because Rosetta adds a translation
cache to its anonymous memory.

These are the owners of the memory 60 seconds after the recorded load on the current
image. The GC value comes from `dotnet-counters` in the same container.

| Owner | Resident | Kernel class |
| --- | ---: | --- |
| Mapped assemblies (`*.dll`), ReadyToRun | 151,028 kB | File, reclaimable |
| GC heap (committed) | 64,512 kB | Anonymous |
| Other unnamed memory (runtime, glibc arenas, thread stacks) | 39,360 kB | Anonymous |
| glibc main heap | 5,484 kB | Anonymous |
| `/memfd:doublemapper` data (loader heaps, stubs) | 60,420 kB | Shmem |
| `/memfd:doublemapper` code (JIT code) | 19,028 kB | Shmem |
| Native libraries (`*.so`) | 25,240 kB | File, reclaimable |

The `doublemapper` memfd is the runtime's write xor execute mapping of executable
memory and loader data.

## Measured owners and levers

The table gives anonymous plus shmem memory after the recorded load and 60 seconds
of rest. Three runs of the same image give 200.8 to 212.4 MB, so a difference of
less than about 12 MB is noise.

| Run | Anonymous plus shmem | Result |
| --- | ---: | --- |
| `f84ab32cf` (production image) | 241.8 MB | Reference |
| `f84ab32cf`, 10 times the load | 267.3 MB | Shmem grows from 103,832 kB to 122,976 kB |
| Current image | 200.8 to 212.4 MB | ReadyToRun moves JIT code to file pages |
| Current image, 10 times the load | 222.8 MB | Shmem stays at 75,488 kB to 79,752 kB |
| `MALLOC_ARENA_MAX=2` | 224.7 MB | No decrease |
| `DOTNET_TieredCompilation=0` | 219.4 MB | No decrease |
| `DOTNET_TC_CallCountThreshold=1000` | 207.3 MB | No decrease |
| `DOTNET_GCHeapHardLimit` of 128 MiB | 219.7 MB | No decrease |
| Sentry DSN to a local sink | 227.1 MB | About 15 MB more anonymous memory |
| PostHog key to a local sink | 223.0 MB | About 11 MB more anonymous memory |
| Sentry and PostHog | 236.7 MB | About 25 MB more anonymous memory |

These are the conclusions:

- The JIT and loader memory of the production image grows with the variety of requests. ReadyToRun with TieredPGO off stops that growth.
- The JIT settings do not move the number. `MALLOC_ARENA_MAX=2` does not move it 60 seconds after the load, but it does after an hour of uptime. See [Production owners](#production-owners).
- The GC heap commits 66,060,288 bytes under the recorded HTTP load. The heap hard limit is a ceiling, so a lower limit does not lower the number.
- Sentry and PostHog add about 25 MB together. Production needs both, so they stay.
- `Microsoft.CodeAnalysis` was never mapped at runtime. The API project no longer references it directly, so the image ships 55 MB less in `/app`. The memory does not change.

The in-process recorded load in `tests/Orbit.Memory.Tests` retains 45,913,448 bytes
of managed memory after a diagnostic full GC in the 512 MiB, half-CPU container.
With the library tokenizer, it retained 83,647,248 bytes. Production logged up to
144 MB of managed memory before these fixes, so the 256 MiB heap limit stays.

The working set after the recorded load is 366 to 398 MB. It is above 300 MB
because ReadyToRun puts about 150 MB of precompiled code in clean file pages, which
the kernel reclaims under pressure.

## Production owners

[`evidence/ticket-2003-live-heap.json`](evidence/ticket-2003-live-heap.json) records
the measurements in this section. Production logged these values on instance
`25dlm` of image `a93b9ace8`, 3 hours and 22 minutes after startup:

| Read | Startup | 3 hours 22 minutes |
| --- | ---: | ---: |
| `rollupKb.Anonymous` plus `rollupKb.Pss_Shmem` | 82,275 kB | 348,668 kB |
| Live GC heap after the last full GC (`gc.lastFull.promoted`) | 25,336,648 bytes | 105,034,640 bytes |
| Large object heap size | 0.2 MB | 37.2 MB |
| Anonymous memory outside the committed GC heap | 6,424 kB | 90,280 kB |
| JIT-compiled methods (`runtime.jitMethods`) | 6,315 | 64,004 |

The highest read of that instance was 360,903 kB, 3 hours and 59 minutes after
startup.

### Uptime soak

[`api-memory-soak.sh`](../.github/scripts/api-memory-soak.sh) reproduces that
production state locally. It runs the image in the 512 MiB, half-CPU container
with the recorded HTTP load. Then, about once a minute, it runs
[`api-memory-soak-client.mjs`](../.github/scripts/api-memory-soak-client.mjs). Each
round reads Today, a habit, its metrics, the profile and the month. It also asks
for the daily summary, creates and deletes a habit, and sends two AI turns.
The fake answer is not a usable summary, so each summary request logs
`AI returned an unusable daily summary` three times and then answers 400.
[`api-memory-fake-openai.mjs`](../.github/scripts/api-memory-fake-openai.mjs)
answers the moderation and Responses calls over TLS like OpenAI. Thus each
AI turn passes moderation, and the cost estimator counts its tokens. The same
TLS sink receives the Sentry and PostHog traffic.

Every 5 minutes, the soak adds up the resident memory of `/proc/1/smaps` by mapping
kind. `dotnet-counters` records the GC and JIT counters every minute. At the end,
`dotnet-gcdump` forces a full GC and reports the live heap by type. The `JIT
summary` reads come from `DOTNET_JitStdOutFile` and `DOTNET_JitDisasmSummary`.

```sh
ORBIT_MEMORY_PLATFORM=linux/arm64 bash .github/scripts/api-memory-soak.sh <image tag> 60 /tmp/api-memory-soak
```

### Owners that the soak names

These are the owners of the production excess, with the measurement that names
each one:

| Owner | Measurement | Fix |
| --- | --- | --- |
| `TiktokenTokenizer` vocabulary | The first token count retains 42,610,848 bytes. After 60 minutes, the gcdump shows three vocabulary dictionary arrays of 25,234,456 bytes in the large object heap. It also shows about 200,000 more strings and 200,000 more byte arrays than the fixed image. | `O200kTokenCounter` keeps the vocabulary in four arrays and retains 4,855,144 bytes. |
| Validators that compile their rules on each request | 50 validations of `CreateHabitCommand` in new scopes compile 402 methods. 50 constructions of the scoped `UpdateHabitCommandValidator` compile 153 methods. | Validators without constructor dependencies are singletons, and `SharedHabitRules` takes delegates. `UpdateHabitCommandValidator` includes two static validators for the rules that need no repository. Both measurements fall to no method. |
| The daily summary compiles a query expression | `GetHabitScheduleQuery` called `Expression.Compile` on `StaleBadHabitsFor` for each read with loaded summary habits. The soak does not isolate its cost. | `HabitScheduleService.IsStaleBadHabit` filters the loaded habits. |
| Database settings bound on each scope | 10 request scopes read the `Database` configuration 90 times. The runtime emits the setter invoke stubs again when its reflection cache is collected. | The connection strings and the transaction timeout are computed once at registration. |
| glibc malloc arenas | In the pair below, the image without a limit has 55 arena heaps at the last read, about 40 minutes after the first read. They hold 78,236 kB, up from 63,988 kB. glibc creates an arena for each thread that allocates natively, up to 8 for each processor. | The image sets `MALLOC_ARENA_MAX=2`. At the same read, the fixed image has 3 arena heaps of 14,928 kB, and its main heap holds 28,880 kB instead of 5,436 kB. |

The remaining live heap is runtime and library state, not request data:

- About 10,500 live `DynamicMethod` objects. They are the EF Core compiled queries, which the EF query cache keeps, and their reader columns. A 6-minute soak with the EF query log on compiles 116 query shapes.
- 1,246 EF model properties and the reflection caches of the runtime.

The JIT summary of the last 3,000 compilations after 20 minutes shows bounded work:
2,943 are tier-1 compilations of methods that tiered compilation promotes once,
and 43 are full-opt compilations. Without the database settings fix, the last 3,000
compilations after 14 minutes had 352 full-opt compilations. 322 of them were the
repeated invoke stubs of the `DatabaseConnectionSettings` setters.

### Before and after

The pair ran at the same time on the same host, with the same harness. Before is
main without these fixes, whose `src` and `Dockerfile` are equal to the production
image `a93b9ace8`. After is `9c4a6d035`, which has every fix except the update
validator change. The pair was planned for 60 minutes. The worker time limit
stopped it at about 41 minutes, so it has no final gcdump. Each run answered 110
`POST /api/chat` requests with 200 in 55 soak rounds.

| Read | Seconds (before / after) | Before | After | Difference |
| --- | ---: | ---: | ---: | ---: |
| Startup | 0 / 0 | 90,788 kB | 87,576 kB | 3,212 kB |
| After the recorded load | 46 / 40 | 215,760 kB | 184,900 kB | 30,860 kB |
| Soak | 385 / 378 | 302,552 kB | 233,140 kB | 69,412 kB |
| Soak | 658 / 652 | 308,096 kB | 237,412 kB | 70,684 kB |
| Soak | 977 / 971 | 314,872 kB | 242,236 kB | 72,636 kB |
| Soak | 1,251 / 1,245 | 317,288 kB | 238,272 kB | 79,016 kB |
| Soak | 1,571 / 1,565 | 314,500 kB | 243,428 kB | 71,072 kB |
| Soak | 1,889 / 1,884 | 321,812 kB | 243,460 kB | 78,352 kB |
| Soak | 2,163 / 2,157 | 321,628 kB | 243,840 kB | 77,788 kB |
| Soak | 2,481 / 2,475 | 324,928 kB | 245,300 kB | 79,628 kB |

The values are `RssAnon` plus `RssShmem`. The warning line is 307,200 kB. Before
crosses it at 658 seconds and stays above it at each later read. After stays at or
below 245,300 kB, 79.9% of the line.

### Levers that stay within the noise

Each lever ran on the image with the tokenizer, validator and summary fixes. The
change compares each soak read from minute 10 with the read of that image at the
same uptime.

| Lever | Change at the same uptime | Decision |
| --- | ---: | --- |
| `DOTNET_EnableWriteXorExecute=0` | 8.7 to 19.5 MB less | Not a fix. It removes the W^X hardening of JIT memory. |
| No `EnableRetryOnFailure` | 6.7 to 15.8 MB less | Not a fix. `VerifiedTransaction` depends on the retrying strategy. |

With `EnableRetryOnFailure`, EF buffers every result. Thus each query shape
compiles one reader lambda for each column, 5,370 of the 12,666 dynamic methods in
the JIT summary. The lambdas are bounded by the query shapes, so they do not grow
with uptime.

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
on the image that the job builds. It does these steps:

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

These unit tests keep the owners in [Owners that the soak names](#owners-that-the-soak-names) from coming back:

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

## Release verification

After the orchestrator releases the API image, read Render's
`orbit-api > Metrics > Memory` for a complete 24-hour window. Inspect
`orbit-api > Events` for any `server_failed` event whose reason contains
`oomKilled`, and search the logs for `API memory budget exceeded`. Each warning
gives the anonymous plus shmem total, and the `API memory breakdown` line after it
gives the owners. Record that window and its maximum in the
owning ticket. The production acceptance criterion remains open until this is
complete.
