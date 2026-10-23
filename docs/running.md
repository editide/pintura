# Running Pintura

Pintura has a local preview server for people and headless commands for agents and scripts.

Presentation commands take the folder holding `presentation.yaml` before any options.
`--help` and `--docs` work without one.

## Install the command

```bash
scripts/install-from-source.sh
```

The script builds Pintura in place and links `~/.local/bin/pintura` to the build, so run it again after changing the C#.
`scripts/install-windows-from-wsl.sh [folder]` publishes a Debug build for Windows to `%USERPROFILE%\dev\pintura`, or the folder given, for Windows apps such as Claude Desktop that can't reach WSL; rerun it after any change, page included.
Users install a release instead, with `curl -fsSL https://raw.githubusercontent.com/editide/pintura/main/install.sh | sh`, or `irm https://raw.githubusercontent.com/editide/pintura/main/install.ps1 | iex` on Windows, which put a self-contained build in `~/.local/lib/pintura` or `%LOCALAPPDATA%\Programs\pintura`; `PINTURA_RELEASE_URL` points either at other archives.
The preview, checks, screenshots, and the HTML and PDF exports need no account; `--export`, `--import`, and `--diff` against a `.pptx` run on editide's API and need an API key.
`pintura --login` reads one from the dashboard, unechoed, or from standard input, checks it with editide's `whoami`, and keeps it in `pintura/account.json`: on Windows in the local application data folder, which stays on the machine, encrypted so only that Windows account can read it, and elsewhere under the user's config folder, readable only by its owner; `EDITIDE_API_KEY` stands in for the key while it's set, `--whoami` shows the plan and the slides exported this billing period, and `--logout` forgets the key.
Pushing a `v*` tag runs `.github/workflows/release.yml`, which publishes trimmed, self-contained, multi-file builds for Linux, macOS, and Windows, installs and checks each on its platform, and attaches them to a GitHub release of that tag, the newest of which the install scripts fetch.
The build stays inside the repository because a development build serves the page from source.

## Read the format

```bash
pintura --docs
pintura --docs slides
```

The first prints the index of topics, and the second prints one topic whole.
Every load error names the topic that explains its fix.

## Preview a presentation

```bash
pintura presentations/example
```

The command opens the presentation in a browser tab, normally at `http://localhost:5180`, and redraws it whenever a file of the presentation or its theme is saved.
A file that fails to load shows its error in the page: a broken slide shows it in place of the slide, and a broken theme or `presentation.yaml` shows it above the last slides that loaded.
The arrow keys, Page Up, Page Down, Home, and End move between slides, and the address ends in the current slide's stem, such as `#revenue`.

The page is a light workspace, with a dark one a click away at the foot of the sidebar; presenting is always dark.
The toolbar above the slide switches views, and each view has a key:

| Key | View |
| --- | --- |
| `S` | One slide at fit. |
| `G` | Every slide, grouped by section; click one, or press Enter, to open it. |
| `T` | The theme: its colors, fonts, text styles, layouts, table and chart styles, and number formats. |
| `O` | Overlays: the slide's areas in blue and where the layout puts the title and placeholders in red, solid where the slide fills them and dashed where it leaves them empty; the inspector's slide header toggles them too. |
| `I` | The inspector, with a count of the slide's issues on its button. |
| `M` | Hides or shows comments. |
| `N` or Shift+`N` | The next or previous slide with comments. |
| `P` or F5 | Present: the current slide full screen, advanced by the arrow keys, Space, or a click, and left with Escape. |
| `\` | Hides or shows the sidebar. |
| `\|` | Moves the slides from the sidebar to a strip below the slide, for a narrow window, or back. |
| `?` | Lists every shortcut. |

Ctrl with the wheel, or a pinch, zooms the slide about the pointer, from half its fit to eight times it, and Ctrl with `+` and `-` steps the zoom about the middle, while Ctrl+0 fits it again.
The zoom stays from slide to slide, and the wheel scrolls a slide zoomed taller than its view instead of moving between slides.

With nothing selected, the inspector shows the slide: its layout and section, its `--check` issues, what changed since the revision compared against, and its elements, its own above those from the layout and theme.
Click an issue or an element there, or an element on the slide, to select it.
Clicking the issues' heading folds them to their count by level, and they stay folded until it is clicked again.

With an element selected, it shows what a reviewer acts on first: the element's issues, what changed, and each linked number with a button to open it in Libreta.
A card then sums the element up by kind: text gives its style, font, and color and how full its box is, or how far it overflows; a chart gives its data with each series in its color, where clicking a value, or its bar, slice, point, or label on the slide, picks it; a table gives a map of its header, styled rows, and merged cells, where clicking a cell picks it; and a picture gives a thumbnail.
A picked cell or chart value gets a section of its own below the card, with a button to open it in Libreta when it comes from a book.
Below the card, the element's values fall into sections with icons, such as Placement, Shape, and Text, or a chart's Data labels, Axes, and Bars, and a table's Columns and Rows, in plain words and points.
Each section folds to a one-line summary when its head is clicked, and stays folded for that kind of element until it is clicked again; a chart's and a table's placement and the sections read least start folded.
What the element's own file sets shows at full weight with its line, and what it inherits folds beneath one line naming where from, such as the callout style and the theme, each value naming its source when unfolded; clicking a line or a source copies the file and line.
Each paragraph is a line beneath, unfolding to what it sets beyond the box and what its runs set beyond it; the paragraph clicked on the slide unfolds by itself.
`--element` prints the same account with every path, for agents.
Escape clears the selection, and dragging the edge of the inspector or the sidebar sets its width, or the top of the strip its height, which a double click resets.
In a window narrower than 1000 pixels, the inspector floats over the slide's right edge rather than shrinking it.
The page remembers its view, overlays, sidebar or strip, filter to changed slides, inspector, their widths, and workspace between visits.

## Review the theme

The theme view shows what the theme defines rather than one element at a time, for reviewing it as a whole.
Each value shows only how it looks and how many slides use it; clicking it shows it in the inspector, with the file and line that sets it, which a click copies, its checks, and chips for the slides using it.

- The sheet: the twelve color slots grouped as text and background, accents, and links, with the named colors and any hex value a slide writes outside the palette; the heading and body fonts; and each layout drawn alone with its title and placeholders holding their names and the areas it adds.
- Worth a look: two slots holding one color, a font this machine lacks, hex outside the palette, and how many values are unused.
- Text styles: the base text, then each style that sets a font or size as a scale of names drawn in their own look from largest to smallest, which expands to a line each of text the deck sets in it; styles setting only color, weight, or alignment follow as modifiers, listing what they change.
- Tables, charts, and formats: a sample table per table style, the series colors in order, and every format spelling the same figure, positive and negative.

Unused colors say so, and other unused values fade, in a list folding behind a count.
In the inspector, a color shows PowerPoint's shades, a layout the theme's areas too, and a style its deck text with what it sets apart from what it takes from the base text.

It only reads, but for a layout's Edit, which opens it on the slide view to move (see below); change the rest of the theme in its files, and the view redraws on every save.
`--screenshot theme` takes a picture of it.

## Edit with the mouse

In the one-slide view, the mouse moves, resizes, and rewords what the slide owns, and each edit is written to the slide's file as a patch to one value, the same one-line diff an agent would make.

- Drag an element to move it, or a handle to resize it; Shift keeps a drag horizontal or vertical and a corner's proportions, and Alt turns off snapping.
- Edges and middles snap to the slide's, to areas and placeholders, and to other elements, with a pink line where they meet.
- An element may be dragged past the slide's edges, where it is drawn faded and `--check` warns that it runs off the slide.
- The arrow keys nudge the selected element a point, or ten with Shift, and write once they rest; with nothing selected they still move between slides.
- A line moves whole, or by the handle on either end.
- Drag across the slide's empty space, or the pasteboard around it, to select what the box touches, passing over an element that holds the whole box and what the layout and theme lock; Shift or Ctrl adds to the selection.
- Shift- or Ctrl-click an element to add it to the selection or take it out; Ctrl-click on a number linked to Libreta still opens it there.
- Several selected elements drag, nudge, and delete together, snapping by the box around them all, as one edit that one Ctrl+Z undoes; they have no handles.
- Delete or Backspace removes the selected elements from the slide's file; the title and placeholders come from the layout and stay.
- Double-click text, or click it once it's selected, to reword it in place, then click away or press Escape to write it, and Ctrl+Z to take it back.
- While rewording, Enter breaks the line in the title or a placeholder, and elsewhere starts a paragraph at the caret, at the level and in the style of the one it splits; Backspace at a paragraph's start, or Delete at its end, joins it to its neighbour.
  Fields such as `${date}` stay as written, and an edit that would cross formatting, such as the edge of a bold run, is refused.
- While rewording, a bar above the text, or Ctrl+B, Ctrl+I, and Ctrl+U, makes the selected words bold, italic, or underlined, writing `**`, `*`, or `[…]{underline=true}` around them, or takes the mark off where all of them have it.
  Text a style makes bold or italic is changed in the style, and the title and placeholders take no formatting, since the layout decides their look.
- The text box button in the toolbar, or X, turns the pointer into a crosshair; drag across the slide to draw a box, snapping as a move does, and type its words straight in.
  The box is written last on the slide, in front of the rest, as `id: text`, `text: ""`, and its `frame`, in the theme's `text` style, and goes again if it's left empty; Escape or X again puts the tool away.
- With a text box, shape, or line selected, Fill and Outline at the top of the inspector open the theme's colors and their shades, its named colors, and PowerPoint's outline weights; hovering one previews it, and clicking it writes the element's own `fill` or `stroke`, such as `fill: accent2 lighter 40`.
  An outline picked where there was none gets `dark1` or a 1-point weight to go with it, and a fill or outline the element's style sets is overridden on the element, not changed in the style.
- With a text box, a shape with text, the title, or a placeholder selected, Align, below Fill and Outline, opens a grid of nine places, left, center, or right by top, middle, or bottom; hovering one previews it, and clicking it writes `align` and `valign`, only the one that changes, on the element or as `title: { text: …, align: center }`.
  A paragraph whose own style sets `align` keeps it, and a notice says which style and where to change it.
  On a text box or shape, Style lists the theme's named styles, each drawn in its own font and color, and picking one writes `style: caption`.

Drag a slide in the sidebar or the grid to reorder the presentation: its line in `presentation.yaml` moves to where it is dropped, and dropping it first under a section's name puts it first in that section.
The move is refused when an agent has moved that slide since, or removed the one it was dropped after.
Delete or Backspace on a slide focused in the sidebar, or the current slide in the grid, removes its line from `presentation.yaml` and its file, and Ctrl+Z writes both back; the last slide stays.

Values round to whole points.
Moving an element placed in an area writes numbers in place of the area's name, and moving the title or a placeholder writes its frame into the slide, as `title: { text: …, frame: [x, y, w, h] }`.
Theme and layout elements don't move on a slide, and the page names the file that owns them; a chart's or a table's text is edited in its files.

Edit on a layout in the theme view opens the layout on the slide view, its title and placeholders holding their names, with its areas drawn dashed and moved by their labels.
The overlays toggle (O) hides the areas, or shows them with the placeholders' frames outlined, and the inspector lists the layout's placeholders, elements and areas, to pick one buried under another.
There the mouse moves and resizes the title, placeholders, areas, and elements, and picks the elements' fill and outline, writing each value in the file that sets it: the layout's own file, or `theme.yaml` for what the layout inherits, which moves it on every layout inheriting it.
The caption counts the slides using the layout, and Back to the theme, or picking a slide, leaves it.

Every patch carries the source it expects to replace and finds it again by its key, so an agent's edit elsewhere in the same file survives.
When an agent changed that value since the page drew it, the patch is refused, and the page says so and redraws from the file.

Ctrl+Z undoes the last mouse edit, on whichever slide it was or a slide's move, and Ctrl+Shift+Z or Ctrl+Y redoes it.
An undo is a patch like any other: it writes back the value as it was, leaving the rest of the file alone, and is refused when an agent has changed that value since.
Undoing a delete puts the element back after the one it followed, and is refused when an agent has removed that one or written an element with the same id.
The history lasts as long as the page; git keeps the rest.

Running the command again for a presentation already served opens the existing server's page, and `--url` prints its address without opening anything.
The server stops about ten seconds after its last tab closes.
`--no-open` opens no tab, for a tool that opens the page itself; a server started with it stops ten seconds later if no page has connected.
Without `--port`, a server takes the first free port from 5180; with it, a taken port is an error.

## Comment on slides

Right-click the slide to comment on it, pinned to the element under the pointer or to the slide itself, then press Enter to save it, Shift+Enter for a new line, or Escape to drop it.
Clicking away saves what was written.
A comment shows as a numbered pin, which follows its element as it moves and opens again on a click to reword, answer, or resolve it; the sidebar and the grid count each slide's comments.

Comments are written to `comments.yaml` beside `presentation.yaml`, never into a slide, so commenting never touches a file an agent is editing.
Each one names who wrote it, from git's `user.name`, and when.
Ask an agent to go through them: it runs `--comments`, makes each change, and marks each comment done, with what it changed, or skipped, with why.
A done pin turns green and a skipped one grey, and opening one shows the agent's note beside your words, so each can be checked against Changes.
Resolve one from its pin, or every done one with Resolve done above the slides, where the open, done, and skipped are counted.
To hand one back to the agent, type a reply into its pin, or press Revert to have the agent undo what it did, with the reason typed beside it if any; the agent marks the reply as it did the comment, and the pin shows the whole thread.
Resolving one moves it, with its thread and how it ended, to `comments-resolved.yaml`, which keeps the history of every request.
A new comment or reply records the last saved version as what you saw, so over unsaved changes its box asks you to save first, with a Save button beside the ask.
Ctrl+Z undoes adding, rewording, replying to, or resolving one, and deleting a slide resolves its comments as removed, which undoing it reopens.
A comment whose element is gone shows as a grey pin, and one on a slide that is gone is listed at the foot of the sidebar to resolve; `--check` warns of both.

## Numbers from a Libreta book

A presentation quoting a Libreta book reads it with the `libreta` CLI, which must be on the path, or where `PINTURA_LIBRETA` points.
The server watches the book's folder too, and any folder linked into it, so restating a fact redraws every title, chart, and table that shows it, with no edit to the presentation.
Holding Ctrl (Cmd on a Mac) underlines the linked numbers, and Ctrl-clicking one opens the book in Libreta at that cell, in the server already running the book or a new one; the inspector has a button for each linked number too.
The book slides in over the preview, live as Libreta serves it, and the slide's name above it, Escape, or the browser's back slides it out, back first undoing what was clicked in the book; the next number selects its cell without reloading the book.
Open in a tab, beside the slide's name, opens the cell in a Libreta tab of the page's own for the book, which each later one brings forward, as a Libreta tab opened elsewhere can't do for itself.
Libreta answers are kept until a file in the book changes, so an edit to a slide starts no Libreta.

## Compare against a revision

In a git repository, the page compares the working tree against `HEAD`, or against the revision its address names, as in `http://localhost:5180/?base=main#revenue`.
The picker beside Save lists the versions of the presentation: the last 50 commits on the current branch that changed it, its theme, or its books, newest first, each numbered from the first as `v1`, `v2`, and on, with its subject and when it was made, under the day it was made.
Choosing one writes it into the address as `?base=`, and choosing the newest, shown as the last save, compares against `HEAD` again.
The picker names the version compared against, as `Since v12`.

## Save a version

Save, beside the theme, commits the presentation and its theme, and nothing else in the repository, whatever is staged; books are Libreta's to save.
Of the files git doesn't have yet, it takes only YAML and the pictures and CSVs the YAML names, so an export or a screenshot left in the folder stays out.
It shows while either differs from `HEAD`, leaving aside the comments, and hides once they don't.
`Ctrl+S` or a click opens it on a description of the changes, such as `Rearranged slide 4`, which typing replaces and Enter saves as the commit's subject.
Outside a repository, Save starts one in the presentation's folder.
The sidebar and the grid mark each slide added, changed, or moved since, and list the slides the revision had and the working tree doesn't under the sidebar.
Two keys review the changes:

| Key | Review |
| --- | --- |
| `B` | Flips the slide, or the grid, to the revision in place and back, with a frame around the slide while the revision shows. |
| `C` | Overlays the changes: added elements outlined in green, removed ones in red where the revision shows, and changed ones in amber, dotted where only the theme restyled them; reworded text marks just the words that changed, with a caret where words were removed, a chart marks the parts that draw its changes, and a table the cells whose look changed. |

The status in the slides sidebar says how many slides changed since the revision, and the toolbar offers `B` and `C` only on a slide that changed, or in the grid when any did; edits to `comments.yaml` alone change none.
Clicking the status shows only the changed slides in the sidebar and the grid, and the arrow keys and the wheel step through just those, until it is clicked again; presenting still shows every slide.
With nothing selected, the inspector lists what changed on the slide and the theme values behind any restyling; with an element selected, it shows each changed value before and after, and reworded text once, with the removed words struck through and the added ones in green.
Nothing is editable while the revision shows.

The inspector reverts a change too: an element, the title, or a placeholder, or the whole slide, written back to the slide's file as the revision has it.
An element added since is deleted, and one removed since is put back after the element it followed.
A revert is an edit like any other, so Ctrl+Z undoes it.
Changes from the theme, the layout, or a chart's or table's data are reverted in those files, and a slide added or removed since in `presentation.yaml`.

```bash
pintura presentations/example --diff
pintura presentations/example --diff main --json
pintura presentations/example --diff main..HEAD
```

`--diff` lists the same changes: each slide's own, by element and value, and then each theme or layout value once, with how many elements it restyled on how many slides.
A long text shows only the words that changed, with a little of what surrounds them.
Values compare as drawn, so a style edit reaches every element that takes it, and a slide number that shifts because a slide was added before it is no change.
A slide counts as moved only when it left the order the others keep.
A revision reads its books as the same commit left them, so a restated fact shows as a change; a book outside the repository can't be compared.
An element whose `id` changed counts as removed and added.

```bash
pintura presentations/example --diff edited.pptx
pintura presentations/example --diff edited.pptx --slides 2,revenue
```

`--diff` against a `.pptx` says what was changed in PowerPoint since the export, for an agent to make the same edits in the files; it reads the deck as `--import` does, with an account, and bills nothing.
Alone it prints a line a slide, counting the elements reworded, moved, restyled, added, and removed, and `--slides` lists each change on the slides it names, so an agent reads the changes a slide at a time and runs it again to see the slide clean.
A paragraph added or removed whole is one line with its words.
The export keeps no ids, so slides pair by number and elements by kind, then frame, then text, and each is named by the presentation's id; an element added in PowerPoint lists what it draws.
Values compare as drawn, so a number a reference renders matches the same number typed in the deck, a text element matches the rectangle PowerPoint keeps it as, and a frame within a point of where it was hasn't moved.
What the export can't carry, such as alt text, is left out, and an element the deck's reading couldn't import is listed as not compared.
A slide inserted or deleted in PowerPoint shifts the pairing of every slide after it.

```bash
pintura presentations/example --check --diff
pintura presentations/example --check --diff main..HEAD --json
pintura presentations/example --screenshot revenue --diff main
pintura presentations/example --screenshot all --diff
```

`--check --diff` checks both sides and reports only the findings the change introduced, and counts those it cleared.
A finding is the one already there when the revision had the same kind on the same element, so text that overflows further than it did isn't new.
It exits 1 only when the change introduced an error.
`--json` adds `since`, the revision compared against, and `gone`, the findings cleared.
The revision is drawn by a headless server of its own, so the first run against a commit costs a second browser start.

`--screenshot --diff` draws the slide as the revision had it beside the working tree, with the changes marked on both as `C` marks them, and writes `<slide>.diff.png` unless `--output` names the file.
`all` draws each slide added, removed, or changed, in pairs, and fails when none changed.
A slide the change removed can be named too.
It takes one revision, since the working tree is always the second side.

## Find the presentation

Omitting the folder searches the working folder and up to three folders below it, skipping `bin`, `obj`, hidden folders, and symbolic links.
Exactly one presentation must be found; otherwise Pintura lists the candidates and asks for one.

## Look at a presentation without a server

```bash
pintura presentations/example --list
pintura presentations/example --slide revenue
pintura presentations/example --element revenue takeaway
```

`--list` prints the slides in order under their sections, with their layouts and titles, and exits nonzero when a slide fails to load.
`--slide` prints everything drawn on one slide, back to front: the theme's and layout's elements, the title and placeholders, and the slide's elements, each with its frame.
`--element` prints an element's effective properties, each beside the file and line that set it, then what each paragraph and run changes.
The title is `title`, and a placeholder is its name.

Add `--json` to any of the three for the same content as JSON.

`pintura presentations/example --comments` lists reviewers' comments in slide order, each beside the file and line that writes what it points at; `--json` works here too.
`--comments --resolved` lists the resolved ones in the order they were resolved, each with how its thread ended, and `--resolve <id>,<id>` resolves the ones a reviewer names.

## Look at the rendered slides

```bash
pintura presentations/example --screenshot revenue
pintura presentations/example --screenshot all --output sheet.png
pintura presentations/example --screenshot theme
pintura presentations/example --check
pintura presentations/example --check --export --json
```

`--screenshot` draws the slide in headless Chromium at twice its size in points, writes a PNG, and prints its path, followed by the slide's text findings.
`all` writes a contact sheet of every slide under its section instead.
`theme` writes the preview's theme view, whole, at 1440 pixels wide.
Take screenshots through `--screenshot` rather than a browser of your own: Pintura sets up its browser so that what it draws matches `--check` and the export, which a browser started some other way may not, with fonts that differ or are missing.
Without `--output`, the PNG goes to `screenshots/<presentation>/<slide>.png` in Pintura's own folder for what it can make again: `~/.cache/pintura` on Linux, where the temporary folder is shared between users, and the temporary folder's `pintura` elsewhere.

`--screenshot` also prints the slide's findings.

`--check` reports six kinds of finding, which `pintura --docs checks` lists: structure, such as a slide that fails to load; text, measured in every box; geometry; consistency across slides; comments on a slide or element that no longer exists, or in a file git doesn't track; and, with `--export`, what the PowerPoint export can't reproduce.
It exits 1 when there are errors and 0 when there are only warnings, as Libreta's does, and `--json` prints the findings as JSON, a structure error with its `file`, `line`, and `topic`.
A theme or layout element with a finding on several slides is one finding, named by its first slide.

Both reload the presentation first, so a command run straight after a save sees the save.
A server already running the presentation lends them its browser.
Otherwise the first run starts a headless server in the background, which costs the browser's start of a few seconds once, and later runs reuse it and answer in about a second.
It opens no tab, and stops after five minutes without a request; one started by an older build of Pintura is replaced.
It holds none of the command's handles, so a shell reading the command's output is done when the command is, and the command finds its address in the instance registry.
It writes to `headless-<process id>.log` in that same folder, which the command that started it relays while it runs and names if the server fails to start, and which the server deletes when it stops.
The browser is an installed Edge, or else an installed Chrome, through Playwright's channels.
Where neither is installed, as on WSL, whose Linux can't drive Windows' Edge, the first run fetches Playwright's headless Chromium, about 115 MB and 260 MB unpacked, into `~/.cache/ms-playwright` or wherever `PLAYWRIGHT_BROWSERS_PATH` points.
`pintura --install-browser` fetches it up front, such as for a machine that is offline later, and goes through the proxy `HTTPS_PROXY` names.

## Export

```bash
pintura presentations/example --export-json
```

`--export-json` prints the model the exporter reads: the theme and each layout defined once, and each slide with only what it owns.
Properties are resolved through the styles, with theme colors and fonts kept as references.

```bash
pintura presentations/example --export out/example.pptx
```

`--export` writes a `.pptx` by replaying that model as editide's write tools on a copy of the blank deck Pintura carries, and lists what it can't reproduce, in the user's terms.
The master takes the theme's `name`, which PowerPoint shows as the design's, and which `--import` reads back.
The tools run on editide's API, with the key from `pintura --login` or `EDITIDE_API_KEY`; each picture is uploaded once as an attachment, at most 25 different ones an export, which a presentation with more is refused before anything is uploaded, and the deck is downloaded at the end.
An SVG picture can carry script, so the API takes none: the headless browser draws it as a PNG, at four pixels a point of its largest frame and at most 4096 pixels a side, kept in that folder's `pictures` by the SVG's hash, so only a new or changed SVG costs the browser's start.
An SVG with a `fill` is drawn in it, once for each fill it takes, and each counts as a picture against the 25.
A throttled call is retried each second for up to five minutes, and an API older than the version the fixtures pass on is refused.
An export leaves `<deck>.pptx.pintura.json` beside the deck, with the deck's hash, the design's, and each slide's, so exporting to the same file again rewrites only the slides that changed, moves the ones that moved, and deletes the ones that went; a deck that is unchanged makes no calls at all.
editide bills each slide, layout, and master an export writes, so a re-export costs only the slides that changed; the export writes only the layouts the slides use, and ends by saying what it billed.
A changed theme or layout, a deck edited since, or a missing manifest makes a full export, which writes the master and layouts again, so deleting the manifest forces one.
A new Pintura makes one only when it writes the design differently, which `ExportManifest.DesignMapping` marks.
`--slides cover,3-5` exports only the slides it names, by id, number, or range, in the presentation's order, with the whole theme.
Into a deck an earlier export wrote, it rewrites those slides and leaves its others as they were, even where the files changed since, which a later full export catches up.
It refuses a deck it would have to replace instead: one edited in PowerPoint since, one whose theme or layouts changed, or one Pintura didn't export.

```bash
pintura presentations/example --export-html deck.html
```

`--export-html` writes one HTML file that shows the presentation with no server, for sending to someone with only a browser.
The renderer, its stylesheet and its font, the slides, every picture, and the theme's `fonts/` are inlined, so the slides draw in the theme's fonts whatever the reader has installed.
It opens on one slide at fit, with a sidebar of thumbnails by section that `\` or its button shows; the arrow keys, the mouse wheel, a click on the slide, or a thumbnail move between slides, and `P` or F5 presents.
Each book the presentation reads is inlined too, as `libreta --export` writes it, so the export needs Libreta and fails when it can't export a book.
Clicking a linked number slides the book in over the whole window at that cell, with its calculation and source, and the button naming the slide, Escape, or the browser's back goes back to it.
The reader gets the whole book, not only the numbers the slides quote.

The page's Export button downloads the presentation, named for its folder, as the files on disk have it, and counts a `.pptx` export's progress against about five calls a slide; it keeps its last `.pptx` in that folder's `exports`, so pressing it again exports incrementally too.
PowerPoint is `--export`'s deck, which takes about a second a slide; Web page is `--export-html`'s file.
PDF is a page per slide at the slide's size in points, printed by the headless browser, so its text can be selected and searched.
An export that fails says why in the page.

## Import

```bash
pintura --import deck.pptx out/deck
pintura --import deck.pptx out/theme --theme
pintura --import deck.pptx out/deck --slides 3,5-7
pintura --import deck.json out/deck
```

`--import` writes a presentation folder with its theme inside from a `.pptx`, or from the model's JSON as `--export-json` prints it, with pictures read from beside the JSON; `--theme` writes the theme folder alone, for a deck handed over as the template to use.
`--slides 3,5-7` writes only the slides it names, by number, or by id from the JSON, with the whole theme.
A `.pptx` is read with editide's read tools, which need an account but bill nothing, and the reads are cached in that folder's `reads` until the deck or the API's `/version` changes.
The master the slides use becomes the theme and its layouts layouts, with a single-line text placeholder kept as a placeholder, named for its prompt, and any other as an area; an element on half the layouts moves to the theme.
A master no slide uses is left out, and a deck whose slides use two masters is refused.
Pictures are copied out of the package, and elements take their ids from the names PowerPoint shows in its selection pane, unless PowerPoint made the name up.
A table whose frame starts an area, as wide, takes the area as the room its rows have, and a bar chart's categories and clustered series read top down, where PowerPoint draws the first at the bottom.
It writes the folder, loads it, and states a key wherever the load differs from the JSON, the theme first, then the layouts, then the slides, so no slide repeats what its theme can say.
A style takes what most of the things naming it share, and one the JSON doesn't name is called `paragraph_1` and on, for an agent to rename.
It lists whatever it couldn't write back, and writes only into an empty folder or one that doesn't exist yet, moving the folder in once it loads, so a failed import leaves nothing behind.

## Develop

```bash
dotnet watch --project src/Pintura run -- presentations/example
dotnet test src/Pintura.Tests
node --test src/Pintura.Tests/*.js
```

A Debug build serves `src/Pintura/wwwroot` from source, so an edit to the page reloads it without a rebuild.