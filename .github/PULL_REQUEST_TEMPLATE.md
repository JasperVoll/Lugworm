## Summary

<!-- One paragraph: what is wrong or missing, and what this change does.
     Link the issue if there is one. -->

Closes #

## Scope

This repository contains the documentation site, the issue tracker and the
release artifacts. The plugin source is not published here, so pull requests
are documentation changes only. Report plugin behaviour as an issue instead.

## Checklist

- [ ] The change describes the plugin's actual behaviour, not intended behaviour
- [ ] Front matter is unchanged, or `title`, `nav_order`, `parent` and `permalink` are all still correct
- [ ] Internal links use `{{ '/docs/...' | relative_url }}`, not a hard-coded domain
- [ ] New screenshots follow `assets/images/README.md` and contain no passwords, IP addresses or private paths
- [ ] `bundle exec jekyll build` completes without warnings
- [ ] `python tools/check_site.py` passes
