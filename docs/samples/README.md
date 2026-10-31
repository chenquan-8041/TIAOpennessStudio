# TIA block sample library

One library for every PLC block XML sample: TIA exports, hand-authored samples, and
fragments derived from exports. There is no second sample directory and no duplicate file.

## Files

| File | Owner | Purpose |
|---|---|---|
| `README.md` | humans | Rules and workflow (this file). |
| `annotations.json` | humans | Manual metadata keyed by sample id. |
| `manifest.json` | scanner | Generated machine index. Never hand-edit. |
| `V<version>/<language>/*.xml` | humans | The sample payloads. |

`manifest.json` is the only machine-readable index. There is no separate `INDEX.md`; read
`manifest.json` instead.

- The version directory is the TIA version the sample was **produced in**, not every version
  it is compatible with. Compatibility is the `schema_bucket` field (`<=15.1` / `>=16`),
  proven by the XSD version.
- Language directories are fixed: `LAD`, `FBD`, `GRAPH`, `SCL`, `STL`, `DB`, `UDT`.

## How to use

### Humans

1. Open `manifest.json` and filter `samples[]` by `language`, `version`, or `features`.
2. Check `status` before trusting a sample:
   - `exported` — came out of TIA and compiles there.
   - `schema_valid` — only validated against the V15.1 XSD.
   - `unverified` — nothing checked yet.
3. Read the XML at `path` only after choosing the sample by metadata.

### Programs and agents

- Read `docs/samples/manifest.json` only. Do not infer provenance, status, or role from
  directory names or file names.
- Each entry exposes: `id`, `path`, `version`, `schema_bucket`, `language`, `block_kind`,
  `provenance`, `status`, `role`, `features`, `parts`, `networks`, `size`, `sha256`,
  `source_block`, `notes`, `duplicate_of`.
- Only `role=template` may be used as a generation skeleton; `role=reference` is read-only.
- `status` is evidence, not a preference: never present an `unverified` sample as ground
  truth. `duplicate_of` marks byte-identical copies.
- No agent tool reads this directory yet. When one is added, it must read `manifest.json`
  and follow the rules above.

## Adding a sample

1. Put the XML in `docs/samples/<version>/<language>/`, named `<topic>_<form>.xml`.
2. Add an entry in `annotations.json` with `provenance`, `status`, `role`, and optional
   `source_block` / `notes`.
3. Run:

```powershell
python utils/scan_reference_assets.py --samples --lad-parts
```

`manifest.json` is rebuilt automatically; `features` are derived from the XML content and
must not be written by hand. After a real `compile_block` passes in TIA, change `status`
to `exported` (or to a stronger evidence level); do not move the file.

## Metadata (`annotations.json`)

| Field | Values | Meaning |
|---|---|---|
| `provenance` | `tia_export` / `authored` / `derived` | Exported from TIA / hand-written here / sliced from an export |
| `status` | `exported` / `schema_valid` / `unverified` | Usable in TIA / V15.1 XSD only / nothing checked |
| `role` | `template` / `reference` | May be used as a generation skeleton / read-only reference |

A file without an annotation is treated as `unknown` / `unverified` / `reference`.

## Feature tags (derived automatically)

| Category | Tags |
|---|---|
| Scope | `local_variable` / `global_variable` / `typed_constant` / `literal_constant` |
| Interface | `interface_input` / `interface_output` / `interface_inout` / `interface_static` / `interface_temp` / `interface_return` |
| Elements | `contact` / `pcontact` / `negated_contact` / `coil` / `set_reset` / `or_branch` / `timer_ton` / `timer_tof` / `counter_ctu` / `move` / `compare` / `block_call` |
| Structure | `single_network` / `multi_network` |
| GRAPH | `graph_sequence` / `graph_transition` / `graph_branch` / `graph_jump` |
| DB/UDT | `db_optimized` / `db_standard` / `instance_db` |

## Hard rules

1. **One file, one copy.** Runtime templates must be embedded from this library; never keep
   a second copy elsewhere.
2. **Mark unverified samples.** A sample whose `status` is not `exported` must not be treated
   as authoritative by the agent.
3. **No partial samples.** An IR missing networks would silently drop networks on rebuild
   (`TryRejectTruncatedIr` rejects it).
4. **Re-run the scanner** after adding, moving, renaming, or deleting samples; otherwise
   `manifest.json` and the files disagree.
5. **No source- or status-based directories or filename prefixes.** Provenance and evidence
   live only in metadata.

## Related indexes

- `docs/PublicAPI/` — TIA API material (XSD, DLL, XML docs, PDF); no block samples.
- `TIAOpennessStudio/TIAOpennessStudio.csproj` — embeds `V15.1/GRAPH/graph_V0.1.xml` as
  `Link="Templates/GRAPH_V15_1.xml"`; no copy in the repository.
- `docs/assets/REFERENCE_INDEX.md` — repository-wide reference asset audit (official /
  project / duplicate / broken).
- `TIAAgent/tools/openness_samples/lad_part_catalog.md` — LAD part vocabulary extracted
  from the V15.1 LAD export; consult it before writing new LAD.
