## Summary

<!-- One paragraph: what is wrong or missing, and what this change does.
     Link the issue if there is one. -->

Closes #

## Scope

This is the public release repository for reviewed plugin source snapshots,
documentation, issues, and releases. Keep a pull request focused on one
reviewable public change and call out changes to machine-control safety gates,
package contents, public APIs, or third-party material explicitly.

## Checklist

- [ ] The change describes the plugin's actual behaviour, not intended behaviour
- [ ] Source changes include focused tests, or explain why automated coverage is not practical
- [ ] New or copied third-party code/data includes provenance and compatible license notices
- [ ] Machine-control changes fail closed and identify the hardware validation performed
- [ ] Front matter is unchanged, or `title`, `nav_order`, `parent` and `permalink` are all still correct
- [ ] Internal links use `{{ '/docs/...' | relative_url }}`, not a hard-coded domain
- [ ] New screenshots follow `assets/images/README.md` and contain no passwords, IP addresses or private paths
- [ ] The GitHub Pages deployment succeeds and the rendered changes are checked online
- [ ] The solution builds and the task-relevant test projects pass
