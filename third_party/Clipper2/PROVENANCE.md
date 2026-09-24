# Clipper2 1.4.0 — exact-source rebuild (AUD-REL-002)

- Upstream: https://github.com/AngusJohnson/Clipper2
- Tag: `Clipper2_1.4.0`
- Commit: `736ddb0b53d97fd5f65dd3d9bbf8a0993eaf387c`
- Extracted 2026-09-01 via `git archive Clipper2_1.4.0 CSharp/Clipper2Lib` from a
  local clone of the upstream repository; `LICENSE` (Boost Software License 1.0)
  copied from the same tag.
- The `.cs` sources, `Clipper2.snk` strong-name key and license are byte-identical
  to the tag. Only `Clipper2Lib.csproj` was replaced: the upstream project file is
  kept functionally identical (netstandard2.0, LangVersion 8, nullable, signed with
  the upstream key, assembly version 1.4.0.0) but builds deterministically with
  `DebugType=none` and a `PathMap`, because the published NuGet binary embeds the
  upstream author's local path
  (an upstream user-profile `.pdb` path) in its PE CodeView record, which fails the
  release rule that shipped packages contain no local user-profile paths.
- Why not upgrade instead: the 1.4.0 pin is the geometry foundation for offsets,
  clipping, nesting and infill; a version change would need its own numeric parity
  review. The existing clipping/offset/infill test fixtures pass unchanged against
  this rebuild (assembly identity incl. the strong-name token `ddeb3f68c442d6e4`
  is preserved, so no binding change occurs).
