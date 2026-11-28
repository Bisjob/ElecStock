# KiCad library

Every part can say which KiCad symbol, footprint and 3D model it uses, and the custom ones (not in the KiCad
libraries) are stored on the server. Each PC gets a copy of them through a sync, so a board opens the same
everywhere, and Claude finds the links through MCP.

## Links on a part

Part pane → **KiCad** tab (or `update_part` through MCP):

| Field | Example | |
|---|---|---|
| Symbol | `Device:R`, `MCU_Espressif:ESP32-C3`, `Stock:INA226-Module-v2` | `Library:Name` |
| Footprint | `Resistor_SMD:R_0603_1608Metric`, `Stock:ESP32-C3-SuperMini` | `Library:Name` |
| 3D model | `${ELEC_STOCK}/KiCad/3D/JST_VH_B2P-VH_1x02_P3.96mm_Vertical.step` | only when the footprint has none, or the wrong one |

Links to the standard KiCad libraries are taken as given. A `Stock:` link must name an item of the stock
library, and it is saved with the item's exact case (KiCad names are case-sensitive). The links are searched
like the rest of the part (`R_0603` finds the 0603 resistors); `search_parts` with `filter: no_kicad` lists the
parts that have no symbol or no footprint yet.

## The stock library

Nickname `Stock`, files under `KiCad/` in the folder the path variable `ELEC_STOCK` points to. This is the
layout of the stock folder from before the server, so boards drawn with it keep working:

```
${ELEC_STOCK}/
├── KiCad/
│   ├── Stock.kicad_sym          ← every symbol, merged
│   ├── Stock.pretty/*.kicad_mod ← footprints
│   ├── 3D/*.step                ← 3D models
│   └── README.md                ← the list of items, with their sources
└── elecstock-library.json       ← version and files written by the sync
```

Adding files: **KiCad library** page (menu), or the KiCad tab of a part (the empty links of the part are then
filled with what the upload brings). Accepted: a `.kicad_sym` (one item per symbol), a `.kicad_mod`, a
`.step` / `.stp` / `.wrl`, or a `.zip` of them (what SnapEDA or Ultra Librarian give; other files of the zip are
ignored). A file with the name of an existing item replaces it. Each item keeps a *source* (link, or how it was
made), shown in the library README.

The server never edits the content of the files: symbols are cut out of their library and merged back as they
are. One thing is adjusted when the library is written: a footprint `(model "…/name.step")` whose file is a 3D
model of the library points to `${ELEC_STOCK}/KiCad/3D/name.step`, whatever path it was saved with.

An item cannot be removed while a part links to it, a symbol extends it, or a footprint uses it (3D model).

## Sync on each PC

PowerShell, on every PC that has KiCad (also shown on the KiCad library page, with the server address):

```powershell
irm http://<server>:4002/kicad/sync.ps1 | iex
```

It downloads `/kicad/library.zip` when `/kicad/version` changed, writes it to the folder of the user variable
`ELEC_STOCK` (created as `~\ElecStock-Library` the first time) and sets that variable. Only the files a
previous sync wrote are replaced or removed. Restart KiCad after the first run: an environment variable takes
precedence over the `ELEC_STOCK` set in KiCad → Preferences → Configure Paths.

Once per PC (or per project), the library tables, nickname `Stock`:

| Table | URI |
|---|---|
| Symbols | `${ELEC_STOCK}/KiCad/Stock.kicad_sym` |
| Footprints | `${ELEC_STOCK}/KiCad/Stock.pretty` |

## For Claude and scripts

- `get_part` returns the links, and download URLs of the stock library files it uses.
- `get_kicad_library` lists the items, the parts using each one, the sync command and the upload command.
- Upload (logged as Claude):

  ```bash
  curl -f -X POST --data-binary @ESP32-C3-SuperMini.kicad_mod \
    "http://<server>:4002/kicad/upload?fileName=ESP32-C3-SuperMini.kicad_mod&part=ESP32-C3-SuperMini&source=https://github.com/…"
  ```

The export zip (Settings → Export) holds the library in the same `KiCad/` layout, and the import reads it back.
