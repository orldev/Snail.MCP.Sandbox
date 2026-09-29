# Snail.MCP.Sandbox

An MCP server over streamable HTTP that runs **Python a model wrote, or a whole project's build and tests, in a
container made for that one call**, with no network, a read-only root, no capabilities and hard limits, under gVisor. Built for Snail.Swarm.Agent,
where it runs as the Nomad job `mcp-sandbox` on a node of class `sandbox`.

Four tools. `sandbox_run_python` takes the whole program, the task's files it reads, and how long it may take;
the answer is what it printed, its exit code, and every file it left in `/out`. `sandbox_run_tests` takes a project
as a tar or tar.gz of its working folder, restores, builds and tests it with the toolchain its files call for —
.NET 10, Node 22 or Python 3.12 with pytest — and answers with an outcome, the counts, the failures by name and
the end of the output of the stage that decided it. `sandbox_diagnose` runs nothing
and says whether the node could: the Docker daemon answers, it has the runtime, the executor image is there. The
platform's probe calls it, so a server that starts on a node without gVisor shows as unhealthy instead of green.
`find_tool` says which of them does a thing, asked in a model's own words.

## Finding the tool

`find_tool` takes a task the way a model would put it — "draw a chart from this table", "check the node before I
try again" — and answers with the tools that match: the name to call and every argument each of them takes, with
what it means. When nothing matches, the whole list comes back, because three tools are a better answer than a
wrong one. It carries no `sandbox_` prefix, like the `find_tool` of the other servers of this fleet, so a model
that learned the question on one of them asks it the same way here. A session opened with a grant is answered
only with the tools it was granted: naming one the door will refuse costs a round trip to find out.

The two other layers those servers have — `run_tools` for a sequence of calls and `run_program` for a JavaScript
program that decides as it goes — are deliberately absent here. They exist so that work needing twenty calls
costs one, and `sandbox_run_python` already is that: the program a model writes loops, branches and reads its own
results inside the container. A JavaScript interpreter driving a tool that starts a Python interpreter would buy
nothing, and a sequence of runs shares nothing between its steps — every run gets a container of its own, and its
`/out` is collected and returned before the next one starts.

## What is isolated

| Layer | What stands there |
| --- | --- |
| The node | A node of its own class that holds nothing worth taking: no workers, no working copies, no tokens. Outbound traffic only to the object store. This is deployment, not code — see [The sandbox node](#the-sandbox-node). |
| The kernel | `runsc` (gVisor), a kernel in user space between the program and the host's. The runtime is checked on **every** run; a daemon that does not know it gets a refusal, never a quiet run under `runc`. |
| The container | Network `none`; read-only root; every capability dropped, bounding set included; `no-new-privileges`; user `65534:65534`; memory with no swap on top; CPU; processes; `/tmp` 64 MB, `noexec`. Created for one run, used for one exec, removed in a `finally`. |
| Files in | Signed links only, only from hosts in `AllowedHosts`, no redirects followed, size counted while reading rather than trusted from the header. Copied into the container owned by root and read-only; the downloads are deleted when the call ends, whatever happened. |
| Files out | `/out` is a tmpfs of the container itself, 32 MB, owned by the run user alone, `noexec`. It is read back by `tar` run in the container as that user, the archive held to twice the tmpfs size; up to 10 files come back, links and special files are named, never followed. |
| The node's share | At most 4 runs at once; a call past that waits for a place. Every other limit is per run. |

A container whose server dies mid-run still ends: its own process sleeps for the run's timeout plus 60 seconds
and the container removes itself with its volumes. One the server created but never started is swept at the
server's next start, which is why **one server per Docker daemon** is assumed.

### A test run

A test run keeps every layer of a run above except two, both because a build is not a script. The project folder
is the run user's and writable, since a build writes beside its sources. And the restore stage is given a network
when `RestoreNetwork` names one: packages are fetched there into a volume per toolchain that outlives the run, the
container is then taken off the network and checked to be on none, and the build and the tests run offline. A
container still attached to a network after the restore is removed without building anything. Test runs have
places of their own (`MaxConcurrentTestRuns`) and limits of their own, because one build holds four times the
memory of a program.

The images are the toolchains' own: Microsoft's .NET SDK image and the official Node image; Python projects are
tested in the executor image, which carries pytest. Nothing is built for the sandbox but that one image.

## What is not

- **The server holds the Docker socket, which is root on the node.** Code a model writes never touches it —
  it runs in the containers — but a flaw in the server itself is a flaw on the node. That is the reason the
  node is a class of its own and holds nothing else.
- **Without gVisor, a kernel exploit reaches the host.** Setting `Runtime` to `runc` gives exactly that, and
  every other layer still holds. It is a setting for checking the rest of the confinement on a laptop, not for
  running work.
- **`noexec` stops a program, not an interpreter.** `/out` and `/tmp` refuse to execute a file written there —
  measured on runsc release-20260921.0 and on runc, the live suite passing 22 of 22 under each — but
  `python /tmp/x.py` still runs, as it would anywhere: the layer closes dropped binaries, the kernel and the
  container close the rest. It holds under gVisor because both are tmpfs mounts of the container itself; `/out`
  used to be a Docker volume on a host tmpfs, read back through the archive API, and gVisor served that volume
  through its own file access, where the host mount's `noexec` never reached. A test run's `/tmp` stays
  executable: the SDKs run what they unpack there.
- **gVisor counts processes by itself.** Its threads are not host tasks, so the pids cgroup does not see them —
  500 threads started under a limit of 64. Under `runsc` a run also gets `RLIMIT_NPROC` equal to its process
  limit, which stopped them at 63. Past its memory a gVisor sandbox is killed whole: the exec answers 128 and the
  container ends, which the server reports as 137, like `runc`.
- **The descriptions state the defaults.** The limits the model reads are compile-time text held equal to the
  defaults of `SandboxOptions` by tests. Changing a limit only in configuration makes the description wrong
  for that deployment; change the default.
- **The restore stage runs project code with a network.** MSBuild targets, npm install scripts and a setup.py all
  run during a restore. `RestoreNetwork` is `none` by default, so a restore reads only what the package volume
  already holds; a deployment that gives it a network should give it one that reaches only a package mirror.
- **The package volume is shared by every run of a toolchain on the node.** A project can leave a package in it
  that the next project's restore picks up from the cache. The volumes are named by `CacheVolumePrefix`, and
  removing them costs only the next restore's download.
- **Node runs write no report this sandbox reads**, so a Node answer has an outcome from the exit code and no counts.
- **Outputs are held in memory** while an answer is built: at most twice the size of `/out` per run.
- `/dev/shm` is Docker's default 64 MB tmpfs and is writable by the run; it is memory, not disk, and ends with
  the container.

## The sandbox node

**As of 16 September 2026 there is no node of class `sandbox`.** The job is pinned to that class, so until one
exists it waits unplaced — loudly. Running it on the worker nodes instead is not a workaround: those nodes hold
working copies, credentials and a Vault token, and the first layer of the isolation is gone.

A sandbox node is a Linux host with Docker and gVisor:

1. Install `runsc` as the [gVisor guide](https://gvisor.dev/docs/user_guide/install/) describes, then register
   it with Docker (`sudo runsc install` writes it into `/etc/docker/daemon.json`) and restart the daemon.
2. Check it: `docker run --rm --runtime=runsc alpine dmesg` prints gVisor's own boot lines instead of the host's.
3. Nomad client: `node_class = "sandbox"`, `meta { docker_gid = "<gid owning /var/run/docker.sock>" }`, and in
   the Docker plugin allow volumes, since the job mounts the socket. The platform's job
   (`deploy/nomad/mcp-sandbox.nomad.hcl` in Snail.Swarm.Agent) is placed only where both are set and adds that
   group to the server's container. Nomad itself never starts a `runsc` container here — the server does,
   through the socket — so `allow_runtimes` needs no change. The job declares a service, so the node needs the
   cluster's Consul like any other.
4. Firewall: outbound to the object store and nothing else.
5. Pull the executor image and the toolchain images (`DotnetImage`, `NodeImage`) onto the node — the server never
   downloads them. `sandbox_diagnose` refuses without the executor image and lists as `untestable` every
   toolchain whose image is missing. The server image comes from the registry with a version tag: Nomad's Docker driver always pulls
   `latest`, so an image built on the node under that tag does not start. Then run the live suite under `runsc`
   (below); it passing is what turns this README's "under gVisor" from a design into a fact.

## Contract with the platform

- **Files go in by key.** The model passes the keys of its task's files as `list_objects` shows them. The
  worker, not the model, replaces each key with a signed link good for minutes, and only for keys the task's
  own journal names. A key that reaches this server unsigned is refused with a hint saying so.
  `AllowedHosts` names the object store's address.
- **The answer.** The first content block is JSON: `ok`, and under `data` the `exitCode` (null when the run was
  killed at its timeout), `timedOut`, `seconds`, `stdout`, `stderr`, whether each was cut, the `files` by name,
  media type and size, what was `omitted` and why, and a `note` on how the run ended. Then every file as an
  embedded resource of its own at `sandbox://out/<name>`, in the same order — pictures too, because an image
  block cannot carry a name. The platform stores each under that name, lists it among the task's objects, and
  can sign it for the next call.
- **Tests.** `sandbox_run_tests` takes one `project` key, signed the same way, optionally a `toolchain` and a
  `timeoutSeconds`. Its JSON carries `toolchain`, `outcome` — `passed`, `failed`, `noTests`, `buildFailed`,
  `restoreFailed`, `timedOut` or `nothingToBuild` — the `total`, `passed`, `failed` and `skipped` counts (null when no
  report was written), up to 20 `failures` with `name` and `message`, `seconds`, the deciding stage's `output` and
  whether it was cut, what of the project was `omitted`, the hosts the restore reached as `network` (null when no
  `RestoreProxy` watched it, empty when it reached none), and a `note`. The counts are read test by test from the
  reports the frameworks write — TRX from dotnet, JUnit XML from pytest — never from a console. A repository whose
  `global.json` names Microsoft.Testing.Platform as its test runner is tested in that mode: the report is asked for
  with `--report-trx`, and again with xunit.v3's own `--report-xunit-trx` when the project carries no TRX extension;
  exit code 8, no test ran, is `noTests`. A Python project with no `requirements*.txt` has its `pyproject.toml`
  installed, with its `test` and `dev` extras.
- **Errors.** `isError` means the run did not happen: no runtime, no image, a refused file. A program that ran
  and failed is `ok: true` with a non-zero `exitCode`; exit code 137 is almost always the memory limit.
- **The job.** Port 8826. The socket mounted at `/var/run/docker.sock`, and the socket's group — the node's
  `meta.docker_gid` — added to the container, since the image runs as its non-root `app` user. Settings as
  environment variables below; `AllowedHosts` must name the object store exactly as the workers reach it, because
  a link is signed for that host.

## Configure

Every setting lives in the `Snail` section; as an environment variable, `Snail__Runtime` and so on. The server
refuses to start on settings that confine less than they claim: an empty runtime, a root or non-numeric user,
a limit of zero.

| Setting | Meaning | Default |
| --- | --- | --- |
| `Runtime` | Container runtime the code runs under; the daemon must know it | `runsc` |
| `Image` | Image the code runs in | `snail/sandbox-python:latest` |
| `DockerEndpoint` | Docker Engine API of the node | `unix:///var/run/docker.sock` |
| `User` | Numeric `uid:gid` the code runs as; never root | `65534:65534` |
| `MemoryBytes` | Memory of one run, with no swap on top | `536870912` |
| `Cpus` | CPUs of one run | `1.0` |
| `Pids` | Processes and threads of one run at once | `128` |
| `MaxConcurrentRuns` | Runs on the node at once | `4` |
| `DefaultTimeoutSeconds` | Seconds a run gets when the call names none | `60` |
| `MaxTimeoutSeconds` | Most seconds a call may ask for | `300` |
| `OutputLimitBytes` | Bytes kept of stdout and of stderr each: half from the start, half from the end | `65536` |
| `OutFolderBytes` | Size of `/out` | `33554432` |
| `TmpFolderBytes` | Size of `/tmp` | `67108864` |
| `MaxOutputFiles` | Files returned from `/out` | `10` |
| `MaxInputFiles` | Files one call may pass | `20` |
| `MaxInputBytes` | Largest input file | `67108864` |
| `MaxInputTotalBytes` | All input files of one call | `268435456` |
| `AllowedHosts` | Hosts input links may point at, as `host` or `host:port`; `AllowedHosts__0`, `__1`… | none — every link refused |
| `DotnetImage` | Image a .NET project is built and tested in | `mcr.microsoft.com/dotnet/sdk:10.0` |
| `NodeImage` | Image a Node project is built and tested in | `node:22-bookworm-slim` |
| `RestoreNetwork` | Docker network the restore stage fetches packages over; never `host` or `container:` | `none` — restore from the package volume only |
| `RestoreProxy` | The restore proxy's address as a restore container reaches it, such as `http://mcp-sandbox:3128`; every host a restore asks for is answered as `network` | none — the proxy is off, `network` is `null` |
| `RestoreProxyPort` | Port the restore proxy listens on inside the server, set together with `RestoreProxy` | `0` |
| `CacheVolumePrefix` | Prefix of the package volumes, one per toolchain | `snail-sandbox-cache` |
| `TestMemoryBytes` | Memory of one test run, with no swap on top | `2147483648` |
| `TestCpus` | CPUs of one test run | `2.0` |
| `TestPids` | Processes and threads of one test run at once | `1024` |
| `TestTmpFolderBytes` | Size of `/tmp` in a test run | `268435456` |
| `MaxConcurrentTestRuns` | Test runs on the node at once, apart from program runs | `2` |
| `DefaultTestTimeoutSeconds` | Seconds a test run gets when the call names none, restore and build included | `600` |
| `MaxTestTimeoutSeconds` | Most seconds a call may ask for a test run | `1800` |
| `MaxProjectBytes` | Most bytes a project may unpack to | `268435456` |
| `MaxProjectFiles` | Most files a project may hold | `20000` |
| `WorkRoot` | Folder on the server's own disk where inputs wait for the copy into the container | the temp folder |

## How a run works

The server creates the container without starting it, copies the program and the inputs into a disk volume at
`/sandbox` through the Engine API's archive endpoint, starts the container idling, runs the program as one exec
in `/sandbox/files`, reads `/out` back through the archive endpoint while the container is still alive, and
removes it. Each step is shaped by something measured on Docker 29: files copied before the start survive it;
a tmpfs **volume** can be read back through the API while the container runs, where a plain `--tmpfs` mount
cannot; and the archive has to be buffered before `TarReader` reads it, because reading it straight off the
Docker.DotNet response stream fails after the first entry.

A test run goes the same way with three differences. The project's archive is rewritten before it is copied in:
every entry owned by the run user, a folder entry for every folder a file sits in — Docker keeps the owner an
archive names, and makes an implied folder as root's — links and special files left out and named, a path that
climbs out refused, the declared sizes added up before a byte is copied. The container idles on `sleep`, since the
SDK images carry no Python. And the stages are execs of their own: restore, build and test for .NET and Node, restore
and test for Python, each stopping the run when it fails. A new package volume is handed to the run user once per
server process by a container that runs `chown` as root with `CHOWN` its only capability and no network.

Nothing travels by bind mount, so the server's own container needs no host path to match.

## Build and test

```bash
dotnet test
docker build -t snail/sandbox-python:latest -f deploy/executor/Dockerfile .
docker build -t snail/s3l-sandbox-mcp:latest -f deploy/server/Dockerfile .
docker pull mcr.microsoft.com/dotnet/sdk:10.0
docker pull node:22-bookworm-slim
```

The unit tests fake Docker, the downloads and the containers, and hold the descriptions, the images and this
table to the code. The live suite runs programs that try the forbidden things inside a real container and is
skipped unless the runtime to check is named, so that a machine without gVisor cannot pass it and look like
proof of it:

```bash
SNAIL_SANDBOX_LIVE_RUNTIME=runc dotnet test --filter "FullyQualifiedName~Tests.Live"
```

On a sandbox node, from a container that only borrows the socket:

```bash
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v "$PWD":/source -w /source \
  -e SNAIL_SANDBOX_LIVE_RUNTIME=runsc mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test --filter "FullyQualifiedName~Tests.Live"
```

A tag `v*` builds both images and pushes them to the GitHub Container Registry.

## License

MIT.
