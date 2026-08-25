# OctreeLod

Out-of-core streaming octree LOD builder for point clouds, exporting to
[3D Tiles](https://github.com/CesiumGS/3d-tiles) (legacy `.pnts` content).

Points arrive in batches (never all in memory at once) and are streamed
straight into an octree on disk. Once ingestion finishes, a bottom-up merge
pass builds a level-of-detail pyramid, which is then exported as a
`tileset.json` + `.pnts` dataset.

## Project layout

- **`OctreeLod.Core`** (netstandard2.0 — usable from a .NET Framework 4.7.3
  host) — all algorithm logic: ingestion, splitting, merging, grid
  subsampling, 3D Tiles export. No format-specific I/O beyond what's needed
  for its own on-disk scratch storage.
- **`OctreeLod.App`** (net8.0) — console entry point. Reads a text point
  cloud file, runs both phases, exports the tileset.
- **`OctreeLod.Tests`** (net8.0, xUnit) — unit + end-to-end tests.

## How it works

**Phase 1 — streaming ingest.** Points descend a fixed-bounds octree
(`OctreeIngestionOptions.WorldBounds`, generous enough to contain any
realistic dataset without needing to know the real extent up front). Each
leaf buffers points on disk; once a leaf reaches `SplitThreshold` points, it
splits into 8 children and its buffered points are redistributed —
immediately, mid-stream, not deferred to the end. RAM only ever holds the
tree's metadata (bbox/parent/children/counts, not point data) plus whatever
single leaf is mid-split at that instant.

**Phase 2 — bottom-up merge.** Every node's representative point set is
computed post-order: leaves keep their raw points verbatim; internal nodes
combine their children's (already-merged) sets and grid-subsample them —
one representative point per occupied cell, position = nearest real sample
to the cell center, color = average of the cell's points. Nodes that only
wrap a single real branch (an artifact of starting from a generous fixed
world bound) are structurally elided so the exported tileset doesn't carry
dozens of near-empty wrapper levels.

**Export.** Walks the (trimmed) tree and writes one `.pnts` file per node
plus a `tileset.json` describing the hierarchy (`box` bounding volumes,
`ADD` refinement, `geometricError` derived from grid cell size). Each
`.pnts` stores positions as `RTC_CENTER`-relative float32 offsets, so
precision holds up even far from the coordinate origin.

## Input format

Whitespace-delimited text with a header row:

```
easting northing depth red green blue nx ny nz
-1.363469 2.325671 0.043312 25 23 26 -0.979106 0.094357 0.180136
...
```

`easting/northing/depth` → X/Y/Z, `red/green/blue` → color. Normals
(`nx/ny/nz`) are read but ignored. See `TextPointCloudBatchSource`.

## Running

Edit the constants at the top of `OctreeLod.App/Program.cs`:

```csharp
private const string InputPath = @"D:\Data\your_file.xyz";
private const int BatchSize = 500;
```

Then:

```bash
dotnet run --project OctreeLod.App/OctreeLod.App.csproj
```

Output goes to a temp folder (`%TEMP%/OctreeLodDemo-<guid>/3dtiles`), printed
at the end of the run. Ingest progress prints live (points/sec, node count).

## Testing

```bash
dotnet test OctreeLod.Tests/OctreeLod.Tests.csproj
```

## Tuning knobs

| Knob | Where | Effect |
|---|---|---|
| `SplitThreshold` | `OctreeIngestionOptions` | Max points per leaf before it splits. Also bounds per-split memory/IO. |
| `MaxSplitDepth` | `OctreeIngestionOptions` | Guard against pathological (near-)duplicate point clusters that can never be spatially separated. |
| `WorldBounds` | `OctreeIngestionOptions` | Fixed root extent — must comfortably contain the real data. |
| `gridDivisions` | `MergeEngine` / `Tiles3DExporter` (must match between the two calls) | Cells per bbox edge during subsampling. Smaller → stronger compression, chunkier LOD steps; larger → smoother LOD, weaker compression. Currently a plain literal (`64`) at each call site in `Program.cs`, not derived from `SplitThreshold`. |

## Known limitations / not yet built

- No georeferencing — tileset is plain local Cartesian, no root transform.
- `leaves.bin` and the `merged/` folder are scratch space for phases 1-2;
  nothing downstream reads them again once the 3D Tiles export has run, so
  they're safe to delete.
- No crash-recovery checkpointing during a long ingest run (in-memory
  metadata is lost if the process dies mid-run).
- `gridDivisions` is a manually-kept-in-sync literal, not a single shared
  constant — a mismatch between the merge and export calls would silently
  corrupt exported `geometricError` values.
