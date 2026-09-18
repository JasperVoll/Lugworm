# GitHub presence kit

This folder holds ready-to-deploy content for the public IAMLab
Slicer repository on GitHub: a wiki (`wiki/`) and an issue board
(`issue-board/`). The build does not read this folder. The content is
deployed with the steps below.

The wiki has one page per component (IAMLab-*.md), an index, and guide pages. Maintain these Markdown files against the current component sources. Preserve manually maintained explanations.

Every wiki page that needs a screenshot carries an
`<!-- IMAGE TODO: ... -->` comment describing the capture, next to an
`![...](images/....png)` reference. Once the images are placed in the
`images/` folder of the wiki under those names, the pages are complete
without further text edits. `IMAGES-TODO.md` lists all of them.

## 1. Deploy the wiki

A GitHub wiki is a separate git repository that is created when the
wiki is opened for the first time.

1. In the public repository, enable the wiki under Settings, Features,
   Wikis. Open the Wiki tab once and create any page. This step
   initialises the wiki repository; the content is overwritten in the
   next step.
2. Clone the wiki repository and copy the content:

   ```powershell
   git clone https://github.com/<owner>/<repo>.wiki.git
   Copy-Item docs\github\wiki\* <repo>.wiki\ -Recurse -Force
   New-Item -ItemType Directory <repo>.wiki\images -Force
   Copy-Item <your-screenshots>\*.png <repo>.wiki\images\
   cd <repo>.wiki
   git add -A; git commit -m "Publish wiki"; git push
   ```

3. `Home.md` becomes the landing page. `_Sidebar.md` and `_Footer.md`
   are rendered on every page automatically.

## 2. Deploy the issue board

1. Copy the templates into the repository. Unlike the wiki, the
   templates live in the version-controlled `.github/` folder:

   ```powershell
   New-Item -ItemType Directory <repo>\.github -Force
   Copy-Item docs\github\issue-board\ISSUE_TEMPLATE <repo>\.github\ -Recurse
   Copy-Item docs\github\issue-board\PULL_REQUEST_TEMPLATE.md <repo>\.github\
   ```

   Replace `<owner>/<repo>` in `ISSUE_TEMPLATE\config.yml` with the
   real names before committing.

2. Create the labels that the templates use (requires the `gh` CLI):

   ```powershell
   Get-Content docs\github\issue-board\labels.json | ConvertFrom-Json | ForEach-Object {
     gh label create $_.name --color $_.color --description $_.description --force
   }
   ```

3. Optionally, set up a Projects board for triage as described in
   `issue-board/PROJECT_BOARD.md`.

## 3. Maintenance

- The wiki mirrors the user guide in `docs/user-guide/`. When the guide
  changes, update the affected wiki page as well.