# Contributing to Lugworm

Lugworm is a Grasshopper plug-in for Rhino 8 on Windows. Issues and pull requests
are welcome. Please describe the observed behavior, the expected behavior, and
how to reproduce a problem. Include the Rhino version and target firmware when
they matter. Remove machine addresses, passwords, and personal data from logs and
Grasshopper files before sharing them.

## How changes flow

The GitHub repository is the public home of the code, the documentation site
and the releases. Development happens in the maintainer's internal repository,
whose history is not published. Each release imports the reviewed source into
the public repository as one commit (`source: import Lugworm <version>`), so
the code on GitHub always matches the released package.

Pull requests are reviewed and merged on GitHub. After the merge the maintainer
applies the change to the internal repository with its original author line,
and the next import carries it back out unchanged, so your commit stays in the
public history. A merged change appears in the next release rather than in the
package immediately.

`docs/` holds the developer documentation (architecture, references, changelog,
user-guide sources). `site/` holds the documentation site published at
<https://jaspervoll.github.io/Lugworm/>; edit its pages in place.

## Building and testing

Build the solution with the .NET 10 SDK:

```powershell
dotnet build Lugworm.sln -c Debug
```

Run the Core and tooling tests before opening a pull request:

```powershell
dotnet test Lugworm.Core.Tests/Lugworm.Core.Tests.csproj -c Debug --filter "Category!=Perf"
python -m unittest discover -s tests/tooling -p "test_*.py"
```

Continuous integration builds the solution and runs the Core, framework-smoke
and tooling tests on every pull request. The Rhino-hosted contract gate, the
manual component sheet and hardware checks cannot run on a hosted runner; the
maintainer runs them before a release.

## Rules

Read [docs/ARCHI.md](docs/ARCHI.md) before changing component sockets, saved
settings, G-code, or machine control. Existing Grasshopper documents depend on
stable component GUIDs, socket positions, and integer option codes. Keep changes
focused and add a regression test for a confirmed defect. Do not run automated
machine motion as a test. A maintainer performs the Rhino and hardware checks.

See [docs/dev/RUNNING_IN_VS.md](docs/dev/RUNNING_IN_VS.md) for local setup and
[docs/dev/TESTING.md](docs/dev/TESTING.md) for the test protocol. Contributions
are reviewed under the repository's [MIT License](LICENSE); third-party code
must retain its own license and attribution.
