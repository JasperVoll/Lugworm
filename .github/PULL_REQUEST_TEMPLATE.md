## Summary

<!-- One paragraph: what is wrong or missing, and what this change does.
     Link the issue if there is one. -->

Closes #

## Scope

Keep a pull request focused on one reviewable change. Say explicitly when it
touches machine-control safety gates, component sockets or option codes, saved
settings, G-code output, package contents or third-party material. Pull
requests are reviewed and merged here; the maintainer then applies the merged
change to the internal development repository, and the next release import
carries it back unchanged (see CONTRIBUTING.md).

## Checklist

- [ ] The solution builds without warnings and the Core, framework-smoke and tooling tests pass (CI runs them)
- [ ] A confirmed defect comes with a regression test that failed before the fix
- [ ] No shipped socket was reordered, renamed or removed and no integer option code changed meaning (see docs/ARCHI.md)
- [ ] Machine-control changes fail closed and state which hardware validation was performed, if any
- [ ] New or copied third-party code or data includes provenance and a compatible license notice
- [ ] Documentation describes the plugin's actual behaviour; site pages under `site/` keep valid front matter and use `{{ '/docs/...' | relative_url }}` for internal links
- [ ] Screenshots, logs and Grasshopper files contain no passwords, machine addresses or private paths
