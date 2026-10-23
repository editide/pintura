# AGENTS.md

Pintura is slides in plain text: a presentation is a folder of YAML, rendered as a live preview in the browser.
It is Libreta's sibling, and borrows its stack, conventions and CLI wherever it can.

## Documents

Read [docs/principles.md](docs/principles.md) before proposing a design change, and judge the change against it.

- [docs/topics/](docs/topics/index.md): the format as `pintura --docs` prints it, one topic per file.
- [docs/architecture.md](docs/architecture.md): the object model, the pipeline, and the export's mapping onto editide's tools.
- [docs/running.md](docs/running.md): the server, discovery, and the headless commands.

## Vocabulary

Use the terms in the [README](README.md#vocabulary) precisely: presentation, theme, layout, placeholder, area, field, slide, element, style.
In particular, only text is a placeholder; charts, tables and pictures take an area as their frame.

## Layout

`themes/default`, `themes/acme`, and `themes/meridian` are the three themes, and `presentations/example` is a six-slide board update for Acme, a made-up company, on its own theme, written by hand.
`presentations/board-update` is a five-slide one on Meridian, a banker's theme in Arial with no callouts.
`presentations/default-theme` and `presentations/balance-sheet` are on the default theme, written by agents that read only `docs/topics`.
All four tell Acme's story, and are the fixtures the loader and renderer are built against, and must check clean.
`src/Pintura.Tests/fixtures/faults` seeds one fault per slide for the checks to find, and `src/Pintura.Tests/fixtures/charts` draws every chart type.

## Conventions

A slide names what a thing is, such as `style: caption`; the theme decides how it looks.

The engine knows no design: a KPI tile or a two-column comparison is a layout, area or style in a theme file, not a branch in the renderer.

Units are points, and every default lives in a theme file.

## Running it

```bash
dotnet watch --project src/Pintura run -- presentations/example
```

Start a server only when the user asks to review something, since starting one opens a tab in their browser.
Before killing processes, check for an instance the user is already running with `pgrep -af Pintura`, and match `pkill` patterns tightly enough to spare it.
Headless servers show up there as `--headless-server`; they are safe to kill, and a rebuild replaces them on the next command anyway.
`--list`, `--slide`, and `--element` answer most questions without a server.
When asked to go through the comments, run `--comments`, make each change, and mark each entry in `comments.yaml`, or its last reply when it has replies, `done:` with what changed or `skipped:` with why, leaving the reviewer to resolve or answer it.
Resolve entries only when asked, with `--resolve`, never by deleting them: whichever the reviewer names, or, asked to clear the comments, those marked `done:`, saying which open or skipped ones are left.

## Code

The stack mirrors Libreta's: .NET 10, ASP.NET minimal API, YamlDotNet, and vanilla JavaScript served from source with no build step.
On WSL, use a Linux SDK inside the distribution, never `dotnet.exe`.

`Payload.cs` resolves the model into what the page draws, so change it and `wwwroot/app.js` together.
Export and import draw every fixture as the preview does, except what the export's warnings list.
Gaps in editide's tools are fixed on editide's side; drop the exporter's workaround for each as it lands.
The exporter's notes on those workarounds go through `Workaround`, which release builds compile out.
Tests live in `src/Pintura.Tests`: `dotnet test src/Pintura.Tests` for the C#, and `node --test src/Pintura.Tests/*.js` for the page.

## Style

- Two blank lines before the closing brace of a class with behavior; none for data-only types.
- Markdown gets one sentence per line.
- Comments earn their place or do not appear.
  Write for someone reading the file cold, never narrating the edit — git already records what changed.