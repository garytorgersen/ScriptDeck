# Screenshot capture guide

This folder holds the screenshots referenced from `USER_MANUAL.md`.
The manual is shipped without binaries by default — drop in PNGs with
the filenames below and they'll render in any markdown viewer that
honors relative paths (GitHub, VS Code preview, most IDEs).

## Conventions

- **Format**: PNG. Compresses screenshots well, lossless, universally supported.
- **Source resolution**: capture at 100% DPI (or whatever your dev box runs at). Don't rescale before saving — the viewer downsizes for display.
- **Width**: aim for 900-1200 px wide for full-window shots, 400-700 px for close-ups. Bigger is fine; markdown viewers shrink to fit.
- **Window chrome**: include the title bar when it conveys state (the asterisk in `04-edit-mode.png`, for instance). Crop it out otherwise.
- **Sample data**: use the bundled `Workspaces/sample.json` (or any real workspace you've built) where it makes sense. Real button labels read better than "Button 1 / Button 2."
- **Annotations**: optional. If you do annotate, prefer arrows + text in a contrasting colour (e.g. orange-red on light backgrounds). Don't burn in PII.

## Filename → what to capture

| File                          | Section            | Capture |
|-------------------------------|--------------------|---------|
| `01-main-window.png`          | §1 Layout          | Full main window with a workspace loaded (the sample workspace works fine). Tab with several buttons visible, ideally inside one or more labeled groups. Status bar at the bottom showing the workspace path. |
| `03-toolbar-search.png`       | §3 Toolbar         | Close-up of the toolbar band with a search term typed in (e.g. `Print`). The console below shows yellow-highlighted matches; the grid alongside shows yellow cells. Both Show Console / Show Grid checkboxes checked. |
| `03-rtb-formats.png`          | §3 RTB format      | Four side-by-side panels of the same `Get-Service \| Select Name, Status, DisplayName -First 5` output rendered with `default`, `list`, `table`, `json`. Compose by running it four times and stitching the captures, OR set rtbFormat per shot and crop just the console. |
| `04-edit-mode.png`            | §4 Edit mode       | Tab in edit mode: buttons with red outlines and the orange-red corner triangle visible. Status bar reads "EDIT mode" in orange. Title bar shows the workspace name with a trailing `*`. |
| `04-match-size-menu.png`      | §4 Match Size      | Right-click context menu open on a button. The "Match Width of" submenu is open showing a few siblings labeled `<Label> (W x H)`. |
| `04-save-prompt.png`          | §4 Saving          | The "Save changes to 'X' before closing?" dialog with Yes / No / Cancel buttons. Capture by editing in edit mode then attempting to close. |
| `06-history-dialog.png`       | §6 Run history     | Tools -> Recent Runs dialog. Sortable grid in the top half with several past runs; details panel in the bottom half showing one row's script path, args, captured output. |
| `07-script-editor.png`        | §7 Script Editor   | Full Script Editor dialog. ScintillaNET editor with a non-trivial script (line numbers + syntax coloring visible). Test Inputs grid populated with `computerName=MYBOX`. Output pane at the bottom showing structured results from a recent test run. Status strip showing "Syntax: OK" / "Idle". |
| `03-jobs-tab.png`             | §3 Background jobs | Jobs tab inside the output area. Top: grid with three rows (one Running, one Queued, one Done). The Done row is selected. Bottom: buffered output of the selected job in the black RTB with cyan info / gold warning lines visible. Tab header reads "Jobs (1 running, 1 queued, 3 total)". |

## Adding new images

1. Add a markdown image reference in `USER_MANUAL.md` at the spot where the image belongs:
   ```
   ![Alt text describing what the image shows](docs/images/NN-name.png)
   ```
2. Drop the PNG into this folder with the matching filename.
3. Add a row to the table above so the next person knows what to capture if it ever needs to be re-shot.

The `alt` text is your safety net — viewers without images, screen readers, and future-you who can't remember why this PNG exists all rely on it. Make it specific.
