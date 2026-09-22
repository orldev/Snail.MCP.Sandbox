# Snail.MCP.Sandbox

An MCP server over streamable HTTP that runs **Python a model wrote, in a container made for that one call**,
with no network, a read-only root, no capabilities and hard limits, under gVisor. Built for Snail.Swarm.Agent,
where it runs as the Nomad job `mcp-sandbox` on a node of class `sandbox`.

Three tools. `sandbox_run_python` takes the whole program, the task's files it reads, and how long it may take;
the answer is what it printed, its exit code, and every file it left in `/out`. `sandbox_diagnose` runs nothing
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
| The container | Network `none`; read-only root; every capability dropped, bounding set included; `no-new-privileges`; user `65534:65534`; memory with no swap on top; CPU; processes; `/tmp` 64 MB. Created for one run, used for one exec, removed in a `finally`. |
| Files in | Signed links only, only from hosts in `AllowedHosts`, no redirects followed, size counted while reading rather than trusted from the header. Copied into the container owned by root and read-only; the downloads are deleted when the call ends, whatever happened. |
| Files out | `/out` is a tmpfs of 32 MB owned by the run user, mounted `noexec`. Up to 10 files come back; links and special files are named, never followed. |
| The node's share | At most 4 runs at once; a call past that waits for a place. Every other limit is per run. |

A container whose server dies mid-run still ends: its own process sleeps for the run's timeout plus 60 seconds
and the container removes itself with its volumes. One the server created but never started is swept at the
server's next start, which is why **one server per Docker daemon** is assumed.

## What is not

- **The server holds the Docker socket, which is root on the node.** Code a model writes never touches it —
  it runs in the containers — but a flaw in the server itself is a flaw on the node. That is the reason the
  node is a class of its own and holds nothing else.
- **Without gVisor, a kernel exploit reaches the host.** Setting `Runtime` to `runc` gives exactly that, and
  every other layer still holds. It is a setting for checking the rest of the confinement on a laptop, not for
  running work.
- **gVisor has not been checked yet.** Every live check in this repository passed under `runc` on Docker
  Desktop; `runsc` does not install on macOS. Run the live suite with `runsc` on the first sandbox node before
  trusting it with real tasks.
- **The descriptions state the defaults.** The limits the model reads are compile-time text held equal to the
  defaults of `SandboxOptions` by tests. Changing a limit only in configuration makes the description wrong
  for that deployment; change the default.
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
5. Pull the executor image onto the node — the server never downloads it, and `sandbox_diagnose` says when it is
   missing. The server image comes from the registry with a version tag: Nomad's Docker driver always pulls
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
| `WorkRoot` | Folder on the server's own disk where inputs wait for the copy into the container | the temp folder |

## How a run works

The server creates the container without starting it, copies the program and the inputs into a disk volume at
`/sandbox` through the Engine API's archive endpoint, starts the container idling, runs the program as one exec
in `/sandbox/files`, reads `/out` back through the archive endpoint while the container is still alive, and
removes it. Each step is shaped by something measured on Docker 29: files copied before the start survive it;
a tmpfs **volume** can be read back through the API while the container runs, where a plain `--tmpfs` mount
cannot; and the archive has to be buffered before `TarReader` reads it, because reading it straight off the
Docker.DotNet response stream fails after the first entry.

Nothing travels by bind mount, so the server's own container needs no host path to match.

## Build and test

```bash
dotnet test
docker build -t snail/sandbox-python:latest -f deploy/executor/Dockerfile .
docker build -t snail/s3l-sandbox-mcp:latest -f deploy/server/Dockerfile .
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
