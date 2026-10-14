# <img src="src/Pintura/wwwroot/brand/icon.png" width="36" align="top" alt=""> Pintura – Slides in plain text

A presentation is a folder of YAML files, and your browser shows it live as you or your agent edit them.
Pintura checks every slide for what a reviewer would catch, such as text spilling out of its box or a title sitting lower than on the other slides.
When the deck is ready, it exports to PowerPoint as a deck people keep working in, with real layouts, theme colors and fonts, and native charts and tables.

https://github.com/user-attachments/assets/7333fb90-3236-4850-bb38-3779dab2d1b5

## How it works

A presentation is a folder:

```text
presentation.yaml                  the name, the theme, and the slides in order
slides/revenue.yaml                one file per slide
charts/revenue-by-segment.yaml     a chart's type and style
charts/segment-revenue.csv         its data
```

A slide picks a layout from the theme and places elements on it:

```yaml
# slides/revenue.yaml
layout: content
title: Connectivity revenue nearly tripled in two years

elements:
  - id: chart
    chart: revenue-by-segment
    frame: left
  - id: total
    text: |
      Total revenue of **$18.7B** in 2025, up 33% on 2024.
    frame: [490, 290, 430, 80]
```

```yaml
# charts/revenue-by-segment.yaml
type: stacked_column
style: minimal
data: segment-revenue.csv
data_labels: { format: billions_of_dollars }
```

```csv
segment,2023,2024,2025
Space,3557,3796,4086
Connectivity,3869,7599,11387
AI,2961,2620,3201
```

`frame: left` names an area the theme's layout defines, and `style: minimal` a chart style in the theme; the slide says what things are, and the theme decides how they look.
Units are points.

## Install

On macOS or Linux:

```sh
curl -fsSL https://raw.githubusercontent.com/editide/pintura/main/install.sh | sh
```

On Windows, in PowerShell:

```powershell
irm https://raw.githubusercontent.com/editide/pintura/main/install.ps1 | iex
```

Each release carries its own runtime, so nothing else needs installing.
Screenshots and checks draw slides in an installed Edge or Chrome, or fetch a headless Chromium the first time they need one.

To try the example, clone this repository and open it:

```sh
git clone https://github.com/editide/pintura.git
pintura pintura/presentations/example
```

### With a coding agent

Paste this into Claude Code or another coding agent, from the folder where you want your presentations to live:

```text
Install Pintura from https://github.com/editide/pintura by running its installer: on macOS or Linux,
`curl -fsSL https://raw.githubusercontent.com/editide/pintura/main/install.sh | sh`; on Windows,
`irm https://raw.githubusercontent.com/editide/pintura/main/install.ps1 | iex`.
Read `pintura --docs`, then the topics it lists as you need them.
```

## Start

Every command takes the presentation's folder as a path, here `my-deck` in the current folder:

```sh
pintura --new my-deck              # creates the folder, with the default theme copied in
pintura my-deck                    # the live preview, in your browser
pintura my-deck --check            # what to fix, slide by slide
pintura my-deck --screenshot all   # every slide on one PNG
pintura --docs                     # the format, one topic at a time
```

It is built for agents: an agent edits the files and runs `--check` and `--screenshot` to see its work, and you review in the browser.
There, dragging, resizing, and rewording write the same small diff to the files an agent would, and comments pinned to a slide wait for the agent to act on.

## Export to PowerPoint

Export runs on editide's API and needs an API key, free to create at [app.editide.com/dashboard/keys](https://app.editide.com/dashboard/keys):

```sh
pintura --login
pintura my-deck --export my-deck.pptx
```

Everything else, the preview, checks, screenshots, and the HTML and PDF exports included, runs on your machine with no account.

## The idea

- The agent in the terminal edits files; the browser shows the presentation and redraws on every save.
- Slides name what things are (a title, a caption, a positive number), and styles in the theme decide how they look.
- Numbers are typed, read from CSV, or linked to [Libreta](https://github.com/editide/libreta) books, so a changed fact updates every slide that shows it.
- The mouse handles what a mouse is good at: nudging, resizing, rewording.
  Each of those writes the same one-line diff an agent would.

## Where it comes from

[What's in a PowerPoint file?](https://editide.com/blog/what-is-a-pptx-file/) starts with a folder of text files describing a presentation, then shows that a `.pptx` has the same architecture buried under 5,000 pages of XML.
Pintura is that folder made real: `presentation.yaml` for the presentation properties, `slides/`, `media/`, `charts/`, with CSV and Libreta books in place of embedded Excel files.
Everything the blog calls a minefield (inheritance you can't see, element ordering, defaults that live only inside PowerPoint.exe) is what the format is designed to avoid.

## Documents

- [Format](docs/topics/index.md): one topic per file, as `pintura --docs` prints them.
- [Principles](docs/principles.md): the rules the design is judged against.
- [Architecture](docs/architecture.md): the object model, the pipeline, and how the export lines up with PowerPoint's objects.
- [Running Pintura](docs/running.md): the preview server and the headless commands.

## Vocabulary

- **presentation**: a folder holding `presentation.yaml`, an ordered list of slides on one theme; it exports to one `.pptx`.
- **theme**: a folder holding `theme.yaml` (colors, fonts, and what every slide shares), `styles.yaml`, and its layouts; the equivalent of a `.potx`.
- **layout**: a named variation on the theme: a different title, extra placeholders, areas, elements, or background.
- **placeholder**: text whose position and style a layout owns and whose words each slide writes; the title is the one nearly every layout has.
- **area**: a named rectangle on a theme or layout that elements take as their frame.
- **field**: a value the presentation fills into text, such as `${slide}` or `${footer}`.
- **slide**: a file under `slides/` that picks a layout, writes its title and placeholders, and adds elements.
- **element**: one thing on a slide, layout, or theme: text, shape, line, picture, chart, or table.
- **style**: a named set of text, table, or chart properties, such as `caption` or `positive`, defined in the theme's `styles.yaml`.

## Changing Pintura

Clone the repository and install the [.NET 10 SDK](https://dot.net/download); on WSL, install the Linux SDK inside the distribution and never build with `dotnet.exe`, since it builds on the Windows side, where file locks, processes and ports are out of WSL's reach.
Run from source with `dotnet watch`, which reloads presentations, the page and C# as you edit:

```sh
dotnet watch --project src/Pintura run -- presentations/example
```

`dotnet test src/Pintura.Tests` runs the C# tests, and `node --test src/Pintura.Tests/*.js` the page's.
`./scripts/install-from-source.sh` makes your build the `pintura` on `PATH`, and the installer puts a release back.
On WSL, `./scripts/install-windows-from-wsl.sh [folder]` publishes your build for Windows to `%USERPROFILE%\dev`, or the folder given, for Windows apps that can't reach WSL.
[AGENTS.md](AGENTS.md) holds the vocabulary and conventions, for people and coding agents alike.

## Status

Early.
Issues are welcome; there are no promises on timing.
See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request.

Animations, transitions, SmartArt, video and audio, 3-D, ink, embedded objects, macros, and custom geometry are out of scope.
A deck that needs them is a deck for PowerPoint.

## License

Apache-2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE).
Use it freely, commercially included.
If Pintura is useful to you or your business, a mention of [editide](https://editide.com) is appreciated.

## Authorship

Built by the team behind [editide](https://editide.com).
Our thesis is that agents work better on plain text than generating code for outdated file formats like OOXML.
Pintura applies that to slides, as [Libreta](https://github.com/editide/libreta) does to spreadsheets.
