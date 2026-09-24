# Architecture Documentation Rules

[ARCHI.md](ARCHI.md) documents the Lugworm architecture. After each
task (new feature, refactor, bug fix), determine if ARCHI.md needs updating.

## When to Update

Update after ANY change that alters:

- Project structure (new directories, moved files, new Core modules) — §4 Project Structure
- Technology stack (new NuGet packages, TFM changes, SDK bumps) — §3 Technology Stack
- The component surface (new/removed components, ribbon groups, base-class behavior) — §8
- The Rhino/Core boundary (new crossing points, new Rhino-side responsibilities) — §9
- Slicing modes, toolpath strategies, or seam modes — §10
- G-code dialects, extrusion model, arc handling — §11, §17
- Robot kinematics, checks, or calibration flow — §12, §13
- Heat-method behavior or limitations — §14
- Duet protocol, gates, or session model — §15
- DoE designs, statistics, or table I/O — §16
- Versioning/packaging/distribution process — §18
- Data flow between components — §19 (update the mermaid diagrams)
- Error handling, testing, or performance strategy — §20–22
- Resolution of a documented weak point (machine-config duplication, net48 test gap,
  release drift, stale docs) — §24 Conclusion and wherever it is flagged

## How to Update by Change Type

### Major Feature / Refactor

Review: §2 Overview, §4 Project Structure, §5 Core Architecture Principles, the affected
domain section (§8–§18), §19 Data Flow Diagrams, §24 Conclusion.

### Minor Feature / Enhancement

Update: the affected domain section (§8–§18) and §4 if files/modules were added.

### Bug Fix

Usually no update needed, unless it reveals/fixes an architectural flaw or changes a
documented behavior (e.g. a fallback, a gate, a tree contract).

### Dependency Changes

Update: §3 Technology Stack, and any affected domain section.

## Guidelines

- Be precise and factual — reflect the actual codebase, prose never overrides source (§1 precedence rule)
- Be concise — enough detail to understand, not implementation specifics
- Update the mermaid diagrams in §19 when data flow changes
- Reference actual file paths (`Lugworm.Core/Gcode/GcodeBuilder.cs` style)
- When a known-stale doc listed in §1 gets fixed, remove it from the §1 stale list
- Keep the "known weak points" in §24 honest: add new ones, delete resolved ones
