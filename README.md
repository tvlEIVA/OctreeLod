# OctreeLod

Out-of-core streaming octree LOD builder for point clouds, exporting to
[3D Tiles](https://github.com/CesiumGS/3d-tiles) (legacy `.pnts` content).

Points arrive in batches (never all in memory at once) and are streamed
straight into an octree on disk. Once ingestion finishes, a bottom-up merge
pass builds a level-of-detail pyramid, which is then exported as a
`tileset.json` + `.pnts` dataset.

## Project layout

- **`OctreeLod.Core`** (netstandard2.0 — usable from a .NET Framework 4.7.3
  host) — all algorithm logic, organized by pipeline stage. Dependencies flow
  one way: `Model` → `Ingest` → `Merge` → `Export`; each stage only ever
  references the ones before it.
  - **`Model/`** — phase-agnostic types shared across all three stages:
    `PointRecord`, `BoundingCube`, `NodeRecord`, `StorageLocator`, the
    metadata store, and `OctreeStructureUtil` (the shared
    "is this child empty" rule).
  - **`Ingest/`** — phase 1: `OctreeIngestionEngine` (streaming split
    cascade), `SlabPointStore` (on-disk leaf buffers), ingest options.
  - **`Merge/`** — phase 2: `MergeEngine`, `GridSubsampler`,
    `AdaptiveRootTrimmer`, and the merged-point output store. Reads leaf data
    via `Ingest`'s `IPointBufferStore`.
  - **`Export/`** — `Tiles3DExporter`, `PntsWriter`, `MinimalJsonWriter`.
    Reads representative point sets via `Merge`'s `IMergedPointStore`.
- **`OctreeLod.App`** (net8.0) — console entry point. Runs all three stages;
  input-format-specific reading lives in its own `Sources/` folder
  (`IPointBatchSource` implementations) so a different file format is a new
  class there, not a change to the pipeline. Two so far:
  `TextPointCloudBatchSource` (already-Cartesian easting/northing/depth) and
  `LatLonPointCloudBatchSource` (geodetic lon/lat/height, auto-converted to
  local ENU meters — see Input formats below).
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
precision holds up even far from the coordinate origin. Optionally pass a
`GeoReference` (lat/lon/height) to `Tiles3DExporter.Export` to anchor the
local East/North/Up frame to a real spot on the WGS84 ellipsoid — this
writes a root `transform` (local → ECEF) so 3D Tiles viewers (Cesium,
deck.gl) place the dataset on the globe instead of defaulting to
ECEF-interpreted raw local coordinates, which for typical local-frame
magnitudes lands the whole dataset a few hundred km from Earth's center —
nowhere near the surface. The ingestion/merge pipeline itself stays
coordinate-agnostic (still just X/Y/Z meters); georeferencing is purely an
export-time concern.

## Input formats

Both are whitespace-delimited text, RGB in columns 4-6, normals (columns
7-9, ignored) optional.

**Already-Cartesian** (`TextPointCloudBatchSource`), header row required:

```
easting northing depth red green blue nx ny nz
-1.363469 2.325671 0.043312 25 23 26 -0.979106 0.094357 0.180136
...
```

`easting/northing/depth` map straight to X/Y/Z.

**Geodetic** (`LatLonPointCloudBatchSource`), header row optional
(`hasHeader` constructor flag):

```
lon lat height red green blue nx ny nz
10.000000 58.000000 0 255 100 255 0 0 0
...
```

Streams the file twice: once (O(1) memory — just two running sums) to
compute the mean lon/lat as a centroid, then again to convert every point to
local East/North/Up meters around that centroid — so the rest of the
pipeline works in ordinary Cartesian meters as usual. `source.Reference`
exposes the computed centroid; pass it straight to
`Tiles3DExporter.Export(..., source.Reference)` so the tileset gets a root
`transform` placing it back at the correct real-world location (see
Georeferencing below).

## Running

Edit the constants at the top of `OctreeLod.App/Program.cs`:

```csharp
private const string InputPath = @"D:\Data\your_file.xyz";
private const int BatchSize = 1500;
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

- Georeferencing (`GeoReference` → root `transform`) is opt-in — only wired
  up automatically for `LatLonPointCloudBatchSource` input. If ingesting
  already-Cartesian data (`TextPointCloudBatchSource`) that's also secretly
  geodetic in origin, the caller is responsible for supplying a matching
  `GeoReference` by hand; the pipeline has no way to infer one.
- `LatLonPointCloudBatchSource`'s ENU conversion is a flat-earth
  approximation around one centroid — fine for a dataset a few hundred km
  across, increasingly distorted at continental scale (should switch to
  per-point ECEF conversion, or tile-local reference points, if that's ever
  needed).
- `leaves.bin` and the `merged/` folder are scratch space for phases 1-2;
  nothing downstream reads them again once the 3D Tiles export has run, so
  they're safe to delete.
- No crash-recovery checkpointing during a long ingest run (in-memory
  metadata is lost if the process dies mid-run).
- `gridDivisions` is a manually-kept-in-sync literal, not a single shared
  constant — a mismatch between the merge and export calls would silently
  corrupt exported `geometricError` values.
