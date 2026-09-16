## Summary

<!-- One paragraph: the problem, the approach, and a link to the issue. -->

Closes #

## Checklist

- [ ] `dotnet build IAMLabSlicer.sln -c Release` reports 0 warnings and 0 errors
- [ ] `dotnet test IAMLabSlicer.sln -c Release` passes
- [ ] New behaviour has a test that fails before the change and passes after
- [ ] No shipped `ComponentGuid` has changed; outputs are appended, never reordered
- [ ] `IAMLabSlicer.Core` has no RhinoCommon reference
- [ ] No new NuGet packages, or `THIRD-PARTY-NOTICES.md` is updated and the addition was discussed first
- [ ] Documentation is updated where behaviour changed (`docs/ARCHI.md`, the wiki page, the changelog entry)

## Hardware impact

<!-- Does the change touch the Duet gates, the kinematic checks, homing, or
     the G-code emitters? If so, list the TESTING.md rows that were run and
     the machine. "None, slicer or UI change only" is a valid answer. -->