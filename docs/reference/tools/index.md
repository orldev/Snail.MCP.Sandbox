# Tools

4 tools in 2 families.
The names, descriptions and schemas are generated from the server itself, so they are exactly what the model receives in `tools/list`.

An MCP server over streamable HTTP that runs Python a model wrote, or a whole project's build and tests, in a container made for that one call, with no network, a read-only root, no capabilities and hard limits, under gVisor.

`sandbox_run_python` takes the whole program, the task's files it reads, and how long it may take; `sandbox_run_tests` takes a project as a tar or tar.gz of its working folder and restores, builds and tests it; `sandbox_diagnose` runs nothing and says whether the node could.

| Tool | Family | What it does |
| --- | --- | --- |
| [`sandbox_diagnose`](run.md#sandbox_diagnose) | Running code and tests in a sandbox | Whether this sandbox can run code at all: the node's Docker daemon answers, it has the isolating runtime, and the executor image is there. |
| [`sandbox_run_python`](run.md#sandbox_run_python) | Running code and tests in a sandbox | Run a Python 3.12 program in a fresh container and get back what it printed and the files it wrote. The container has no network, keeps nothing between calls… |
| [`sandbox_run_tests`](run.md#sandbox_run_tests) | Running code and tests in a sandbox | Build a project and run its tests in a fresh container, and get back how many passed and which failed. The toolchain is found from the files: a .slnx, .sln, … |
| [`find_tool`](discovery.md#find_tool) | Finding a tool | Finds tools of this server for a task in words, best first, with the parameters of each. |
