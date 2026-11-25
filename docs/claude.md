# Claude and the stock (MCP)

The server exposes an MCP endpoint at `/mcp`: Claude (Claude Code, or any MCP client) searches the stock,
reads the part sheets and photos, reserves parts for a board, plans builds and fills orders through it. Every
change it makes goes through the same checks as the UI and lands in the history as "Claude".

## Registering the server (once, for every project)

```bash
claude mcp add --transport http --scope user elecstock http://<server>:4002/mcp
```

`--scope user` makes it available in every project folder. Check it with `claude mcp list`. Dev server:
`http://localhost:5190/mcp`.

## Suggested CLAUDE.md section

To paste in the `CLAUDE.md` of your electronics projects (or your user `~/.claude/CLAUDE.md`). The server
already sends a short version of these rules to the client as MCP instructions.

```markdown
## Using the stock

The stock (parts, quantities, sheets, photos, project allocations, orders, locations) lives on the
ElecStock server and is used through its MCP tools (`elecstock` server).

- Before proposing a part, `search_parts` / `check_availability`. Available = qty − reserved; `10+` is a
  lower bound; an empty qty means not counted. Prefer parts in stock; say clearly when something must be
  bought.
- `get_part` gives the technical sheet, my free notes (measurements, tests, wiring) and the photo
  captions and markers (pinouts); `get_part_photos` shows the photos when the pinout or form factor
  matters. These facts override generic datasheet assumptions. Edit the free notes only when asked.
- When a design adopts a stock part, `reserve_parts` on the board revision (project / device / rev, with
  the designators). When a design drops it, `release_parts`. A device `bom.csv` can be reserved in one go
  with `import_bom` (preview first, `apply: true` after review).
- `mark_built` (after `plan_build`) only when I confirm the board is built. `receive_order` only when
  I confirm the parcel arrived.
- New part bought or about to be: `add_part` with qty 0, then `add_to_order` (supplier, unit price).
  Modules and ICs get a sheet with `update_sheet` (template: Key characteristics, Pinout, Used in, Gotchas,
  Links).
- Part notes hold facts about the part, never project usage. A sheet's "Used in" section is plain text
  (e.g. `WeatherStation / Sensor-Board`).
- Every change made through MCP is in the history as "Claude" (`get_history`).
```

## Tools

| Tool | Does |
|---|---|
| `search_parts` | search (words in ref, designation, notes, location, projects, designators, photo captions), category and status filters |
| `get_part` | everything about a part: availability, sheet, free notes, photo captions and markers, project lines, orders, history |
| `get_part_photos` | the photos as images, with captions and marker positions |
| `check_availability` | whether the stock covers a list of `{ref, qty}` |
| `list_projects`, `get_revision` | projects → devices → revisions, lines with their stock state |
| `save_project`, `save_revision` | create / update (a revision creates its project and device when missing) |
| `reserve_parts`, `release_parts` | allocation lines of a revision (upsert / remove) |
| `import_bom` | match a `bom.csv` against the stock, preview or reserve |
| `plan_build`, `mark_built` | preview / decrement the stock for a built board |
| `add_part`, `update_part`, `update_sheet`, `update_free_notes`, `fetch_datasheet` | edit the stock |
| `get_shopping_list`, `add_to_order`, `list_orders`, `get_order`, `receive_order` | buying |
| `list_locations`, `get_history` | storage locations, change history |
