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
- `MALLOC_ARENA_MAX` and the JIT settings do not move the number. The [glibc tunables](https://sourceware.org/glibc/manual/latest/html_node/Memory-Allocation-Tunables.html) limit the arena count, but the arena heaps held only 17 to 45 MB in the
  [native attribution](evidence/ticket-2003-native-memory.json).
- The GC heap commits 66,060,288 bytes under the recorded HTTP load. The heap hard limit is a ceiling, so a lower limit does not lower the number.
- Sentry and PostHog add about 25 MB together. Production needs both, so they stay.
- The `o200k_base` tokenizer already loads on first use. Its vocabulary holds 42 MB of live managed memory after the first count. The library caches it statically, so the API cannot release it.
- `Microsoft.CodeAnalysis` was never mapped at runtime. The API project no longer references it directly, so the image ships 55 MB less in `/app`. The memory does not change.

The in-process recorded load in `tests/Orbit.Memory.Tests` also runs the Astra
ranking turn and the tokenizer. In the 512 MiB, half-CPU container, it retains
83.6 MB of managed memory and commits up to 124 MB of GC heap. A 128 MiB heap limit
lowers anonymous plus shmem memory by 11 MB, but production logged 144 MB of managed
memory. Thus the 256 MiB limit stays.

The working set after the recorded load is 366 to 398 MB on the current image. It is
above 300 MB because ReadyToRun puts about 150 MB of precompiled code in clean file
pages, which the kernel reclaims under pressure.

About 150 MB of the production anonymous plus shmem memory does not appear in these
runs. The local runs have no outbound TLS calls to OpenAI, Firebase, Stripe or SES,
no Hangfire work on real accounts, and fewer hours of uptime.

## Selected configuration

The image publishes the API ReadyToRun for the target architecture
(`-r linux-x64` or `-r linux-arm64`, `-p:PublishReadyToRun=true`, framework
dependent), and `Orbit.Api.csproj` sets `TieredPGO` to `false`. See
[ReadyToRun compilation](https://learn.microsoft.com/dotnet/core/deploying/ready-to-run)
and the [TieredPGO setting](https://learn.microsoft.com/dotnet/core/runtime-config/compilation#profile-guided-optimization).

The GC policy is workstation GC, a 256 MiB heap hard limit, memory conservation
level 7, concurrent GC and tiered compilation. It follows the runtime's
[GC configuration contract](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector).
The production environment group sets no memory variable. The instance plan and
its cost do not change.

## Guards

`.github/scripts/api-memory-container.sh` runs in the `Docker migration bundle` job
on the image that the job builds. It does these steps:

1. It starts `postgres:17` with TLS and applies the migrations with the image's `efbundle` in a separate process.
2. It starts the API with `--memory=512m --memory-swap=512m --cpus=0.5`.
3. It reads memory at startup and after 60 seconds of rest.
4. It runs the recorded HTTP load and reads memory again, at once and after 60 seconds.

The guard fails when the container stops, when the kernel kills it, or when
`RssAnon` plus `RssShmem` reaches 314,572,800 bytes at any read. Set
`ORBIT_MEMORY_PLATFORM=linux/arm64` to run it natively on Apple silicon.

`Orbit.Memory.Tests` runs the in-process recorded load in a fresh test host. The
workload holds 150 slow event subscriptions for 50 accounts. Across 20 rounds, it
reads Today and the daily summary, and runs four bulk actions, an Astra ranking
turn, a tokenizer count and an export of 10,000 conversation rows. Every round after
the first must stay below the same anonymous plus shmem line.

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

## Release verification

After the orchestrator releases the API image, read Render's
`orbit-api > Metrics > Memory` for a complete 24-hour window. Inspect
`orbit-api > Events` for any `server_failed` event whose reason contains
`oomKilled`, and search the logs for `API memory budget exceeded`. Each warning
gives the anonymous plus shmem total. Record that window and its maximum in the
owning ticket. The production acceptance criterion remains open until this is
complete.
