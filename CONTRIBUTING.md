# Contributing to Lugworm

Issues and pull requests are more then welcome. Please describe the observed behavior, the expected behavior, and
how to reproduce a problem. Include the Rhino version and target firmware. Remove machine addresses, passwords, and personal data from logs and
Grasshopper files before sharing them.

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

Contributions are reviewed under the repository's [MIT License](LICENSE); third-party code
must retain its own license and attribution.
