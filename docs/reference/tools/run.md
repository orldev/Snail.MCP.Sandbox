# Running code and tests in a sandbox

## <code>sandbox_<wbr>diagnose</code>

Whether this sandbox can run code at all: the node's Docker daemon answers, it has the isolating runtime,
and the executor image is there. Nothing is run. Call it when sandbox_run_python fails before the program
starts; a program that ran and exited with an error is a fault in the program, not in the sandbox.
untestable lists the toolchains whose image the node lacks: sandbox_run_tests cannot test those projects
until the image is pulled onto the node, and calling again will not change that.

*read-only · repeat-safe · stays on this machine.*

Takes no parameters.

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {}
}
```

</details>

## <code>sandbox_<wbr>run_<wbr>python</code>

Run a Python 3.12 program in a fresh container and get back what it printed and the files it wrote.
The container has no network, keeps nothing between calls and sees only the files you pass: they are in
the working directory under their own names, read-only, so open("sales.csv") reads one of them.
Write charts, tables and reports to /out: up to 10 files, 32 MB in all, come back with the answer.
pandas, numpy, matplotlib, duckdb, openpyxl and pytest are installed; nothing can be installed during a run.
A run gets 512 MB of memory and 1 CPU. Standard output and standard error past 64 KB each keep their
beginning and their end with the middle cut, so print summaries rather than whole tables.
The run is a job: it answers within wait with the result, or with the job to read as a resource.

*read-only · every call adds · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `code` | `string` | yes | — | The whole program. Nothing from an earlier call exists: import, load and compute again every time. |
| `files` | `array?` | — | `null` | Files the program reads: the keys of this task's files exactly as list_objects shows them, at most 20, each up to 64 MB and 256 MB in all. Each one appears in the working directory under its file name. |
| `timeoutSeconds` | `integer?` | — | `null` | Seconds the run may take, from 1 to 300; 60 when omitted. Past it the run is killed and nothing from /out comes back. |
| `wait` | `integer` | — | `60` | Seconds to wait for the run before answering with the job in flight, from 0 to 1860; 60 when omitted, 0 answers at once. A run that ends within it answers as described. One that does not answers with ok true and the job's report as data, its uri a resource whose text is that report; the report ends with the run as its result, every file in it base64. A client that can wait awaits that resource; it is not asked again. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "code": {
      "description": "The whole program. Nothing from an earlier call exists: import, load and compute again every time.",
      "type": "string"
    },
    "files": {
      "description": "Files the program reads: the keys of this task\u0027s files exactly as list_objects shows them, at most 20,\neach up to 64 MB and 256 MB in all. Each one appears in the working directory under its file name.",
      "type": [
        "array",
        "null"
      ],
      "items": {
        "type": [
          "string",
          "null"
        ]
      },
      "default": null
    },
    "timeoutSeconds": {
      "description": "Seconds the run may take, from 1 to 300; 60 when omitted. Past it the run is killed and nothing from /out comes back.",
      "type": [
        "integer",
        "null"
      ],
      "default": null
    },
    "wait": {
      "description": "Seconds to wait for the run before answering with the job in flight, from 0 to 1860; 60 when\nomitted, 0 answers at once. A run that ends within it answers as described. One that does not answers with\nok true and the job\u0027s report as data, its uri a resource whose text is that report; the report ends with the\nrun as its result, every file in it base64. A client that can wait awaits that resource; it is not asked again.",
      "type": "integer",
      "default": 60
    }
  },
  "required": [
    "code"
  ]
}
```

</details>

## <code>sandbox_<wbr>run_<wbr>tests</code>

Build a project and run its tests in a fresh container, and get back how many passed and which failed.
The toolchain is found from the files: a .slnx, .sln, .csproj or .fsproj is built with .NET 10, a package.json
at the root with Node 22, Python files with Python 3.12 and pytest. Packages are restored first; the build and
the tests run with no network. A run gets 2048 MB of memory and 2 CPU.
A project may unpack to 256 MB and 20000 files. The outcome is one of passed, failed, noTests, buildFailed, restoreFailed, timedOut or nothingToBuild.
The answer counts the tests, names the first 20 failures with their messages, and carries the end of the
output of the stage that decided the outcome.
The run is a job: it answers within wait with the result, or with the job to read as a resource.

*read-only · every call adds · may reach outside services.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `project` | `string` | yes | — | The project as a tar or tar.gz of its working folder, without build outputs and dependencies: the key of that archive exactly as list_objects shows it. Nothing else of the task is visible to the run. |
| `toolchain` | `string?` | — | `null` | dotnet, node or python; omitted, it is found from the project's files. Name it when a project mixes them. |
| `timeoutSeconds` | `integer?` | — | `null` | Seconds the whole run may take, restore and build included, from 1 to 1800; 600 when omitted. Past it the run is killed. |
| `wait` | `integer` | — | `60` | Seconds to wait for the run before answering with the job in flight, from 0 to 1860; 60 when omitted, 0 answers at once. A run that ends within it answers as described. One that does not answers with ok true and the job's report as data, its uri a resource whose text is that report; the report ends with the run as its result, every file in it base64. A client that can wait awaits that resource; it is not asked again. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "project": {
      "description": "The project as a tar or tar.gz of its working folder, without build outputs and dependencies: the key of that\narchive exactly as list_objects shows it. Nothing else of the task is visible to the run.",
      "type": "string"
    },
    "toolchain": {
      "description": "dotnet, node or python; omitted, it is found from the project\u0027s files. Name it when a project mixes them.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "timeoutSeconds": {
      "description": "Seconds the whole run may take, restore and build included, from 1 to 1800; 600 when omitted. Past it the run is killed.",
      "type": [
        "integer",
        "null"
      ],
      "default": null
    },
    "wait": {
      "description": "Seconds to wait for the run before answering with the job in flight, from 0 to 1860; 60 when\nomitted, 0 answers at once. A run that ends within it answers as described. One that does not answers with\nok true and the job\u0027s report as data, its uri a resource whose text is that report; the report ends with the\nrun as its result, every file in it base64. A client that can wait awaits that resource; it is not asked again.",
      "type": "integer",
      "default": 60
    }
  },
  "required": [
    "project"
  ]
}
```

</details>

