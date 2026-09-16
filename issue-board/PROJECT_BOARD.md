# Issue board setup (GitHub Projects)

One board with four columns, driven by labels. The setup takes about
ten minutes with the `gh` CLI.

## Create the board

```powershell
gh auth refresh -s project    # once: grants the project scope
gh project create --owner <owner> --title "IAMLab Slicer"
```

In the web interface (Projects, IAMLab Slicer):

1. Rename the options of the default Status field to Triage, Backlog,
   In progress, and Done.
2. Under Workflows, enable:
   - Item added to project: set Status to Triage.
   - Issue closed: set Status to Done.
   - Auto-add to project with the filter `is:issue is:open` on the
     repository, so that every new issue enters Triage automatically.

## Triage protocol for new issues

1. Reproduce the report or ask for more information. A reproducible
   report receives the `confirmed` label; an unclear report receives
   `needs-repro` and stays in Triage.
2. Add an area label (`area:slicing`, `area:duet`, and so on).
3. Add the `safety` label to anything that touches gates, homing,
   trust, kinematic checks, or the G-code emitters. Such issues rank
   above features by default and require a `needs-hardware`
   verification before closing.
4. Move the issue to Backlog when accepted, or close the issue with a
   reason (`wontfix` or `duplicate`, with a link to the duplicate).

Remove the `triage` label once the issue has been assessed. The board
column carries the state from that point on.

## Milestones

Create one milestone per release (`v2.7.0` and so on). An issue is
assigned to a milestone only when the issue is planned for that
release.

## Releases

Tag the release commit and create a GitHub Release with the `.yak`
file attached and the matching section of the Release Notes wiki page
as the description. Issues fixed in the release are closed by the
release commits (`Closes #n`), and the board moves them to Done
automatically.