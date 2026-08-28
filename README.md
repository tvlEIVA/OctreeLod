# OctreeLod

Out-of-core streaming octree LOD builder for point clouds, exporting to
[3D Tiles](https://github.com/CesiumGS/3d-tiles) (legacy `.pnts` content).

Points arrive in batches (never all in memory at once) and are streamed
straight into an octree on disk. Once ingestion finishes, a bottom-up merge
pass builds a level-of-detail pyramid, which is then exported as a
`tileset.json` + `.pnts` dataset.

## Project layout

- **`OctreeLod.Core`** (netstandard2.0 — usable from a .NET Framework 4.7.3
  host) — all algorithm logic. Two independent engines sit side by side,
  each its own top-level folder/namespace root, sharing only `Model` and
  `Export`:
  - **`Model/`** — phase-agnostic types shared by both engines:
    `PointRecord`, `BoundingCube`, `OctreeNode`, `StorageLocator`, `CellKey`,
    `OctreeStructureUtil` (the shared "is this child empty" rule), and
    `INodePointStore`/`NodePointFileStore` — the out-of-core per-node point
    store both engines write their final/paged output through (deliberately
    generic, not "merged" — the spacing engine has no merge phase and uses
    it purely to page nodes to disk mid-ingest). `OctreeNode` is a real
    reference-type tree node — `Parent`/`Children[8]` are direct object
    references, so the whole tree is reachable from just the root (an
    engine exposes `Root`, nothing else — no separate metadata store or
    id-indexed list). Every node still carries a stable `Id`, but only as an
    external handle: `INodePointStore` keys on-disk point data by it, and
    3D Tiles export uses it as the content filename (`{id}.pnts`) — it
    plays no role in in-memory traversal. `Id` is derived from position, not
    a counter: root = 0, child = `parent.Id * 8 + octant + 1` (standard
    complete-8-ary-tree indexing — same arithmetic a binary heap uses,
    generalized from 2 children to 8). Deterministic and collision-free by
    construction, but the id needs ~3 bits per depth level, and both
    engines' `MaxSplitDepth` defaults to 60 — 180 bits at that depth, well
    past a 64-bit range — so `Id` is `System.Numerics.BigInteger`, not
    `long`.
  - **`SplitMergeEngine/`** — the legacy two-phase engine, dependencies flow
    one way: phase 1 → phase 2, each only ever referencing the one before.
    - **`Ingest/`** — phase 1: `OctreeIngestionEngine` (streaming split
      cascade), `SlabPointStore` (on-disk leaf buffers), ingest options.
    - **`Merge/`** — phase 2: `MergeEngine`, `GridSubsampler`,
      `AdaptiveRootTrimmer`. Reads leaf data via `Ingest`'s
      `IPointBufferStore`, writes through `Model`'s `INodePointStore`.
  - **`SpacingEngine/`** — the spacing-based engine:
    `SpacingIngestionEngine`, ingest options. Single streaming pass, no
    merge phase — see "Spacing-based engine" below. Reuses `Model`'s
    `INodePointStore`/`NodePointFileStore` (for out-of-core node paging, not
    a merge) and `SplitMergeEngine`'s `AdaptiveRootTrimmer`, so it shares
    `Export` with the legacy pipeline unchanged.
  - **`Export/`** — `Tiles3DExporter` (`TileRefine.Replace`/`.Add`, caller
    picks — see below), `PntsWriter`, `MinimalJsonWriter`. Reads
    representative point sets via `Model`'s `INodePointStore`.
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
`geometricError` derived from grid cell size). Refine mode is caller-chosen
via `Tiles3DExporter.Export`'s `TileRefine` parameter — `REPLACE` for this
pipeline, since `GridSubsampler` gives every level a spatially-complete
(if coarse) sample of its whole footprint, safe to swap in place of its
children. (The spacing engine below produces the opposite shape — a node's
content is only what its children didn't already capture — and always
exports `ADD`.) Each
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

## Spacing-based engine (PotreeConverter-style)

`SpacingIngestionEngine` is a second, alternate engine (`OctreeLod.Core/SpacingEngine/`)
implementing the LOD rule real PotreeConverter uses: a point is accepted
into the first node — walking down from root — where it lands in a still-
unoccupied voxel cell of that node's own spacing (`cellSize =
node.Bbox.Size / GridDivisions`, same formula and constant `GridSubsampler`
uses, so a cell doubles in size every level up same as the legacy engine's
grid). If the cell is already taken, the point is pushed into the correct
child — created lazily, one octant at a time, only when actually needed —
and the check repeats one level down. Unlike the legacy engine's fixed
`SplitThreshold`-per-leaf split, this decides *which level a point belongs
at* per point, at insertion time.

Because LOD membership is decided during ingestion itself, there's **no
merge phase** — every node's accepted-point set already is its final
representative content the moment ingestion finishes. `AdaptiveRootTrimmer`
+ `Tiles3DExporter` are reused completely unchanged from the legacy
pipeline, reading node content through the same `INodePointStore`
interface the legacy engine's merge phase writes — the constructor takes
this store directly, since paging needs disk access *during* ingest, not
just at the end, and `Program.cs` names its directory `nodes/`, not
`merged/`, since nothing is actually merged here.

One real difference from the legacy engine's `GridSubsampler`: a cell's
representative point is whichever point reached it *first* (no way to know
which point is nearest a cell's center until the stream ends), and it keeps
that point's own color rather than averaging the cell's points — arguably
closer to real PotreeConverter's behavior.

**Locality fast path.** Points don't have to walk from `Root` every time.
A node's cell being occupied for a given point implies every ancestor's
corresponding cell is occupied too — a child cell bucket always nests
inside exactly one specific parent cell bucket (fixed by the cell-index
arithmetic, independent of exact position within it), so whoever occupies
that child bucket was necessarily rejected by that same parent bucket
first. The converse holds too: a free cell at a node means every descendant
is free as well (nothing could have reached a deeper bucket without this
one being occupied first). So for a single point, the occupied/free
sequence from `Root` down is exactly "occupied...occupied, free...free"
with one transition, and that transition is the correct acceptance level —
`ClosestStartingNode` finds it by climbing from the previous point's
landing node (`_lastNode`) toward `Root` only as long as cells keep coming
back free, stopping at the first occupied ancestor (or `Root`). No
approximation: it lands on exactly the node a fresh `Root`-down walk would
find, just without re-checking levels that don't need it.

**Out-of-core via paging.** A point for any node — even one near the root —
can arrive at any time until the stream ends, so no node's accepted-point
set can be considered final and dropped mid-run. Instead, each node's
accepted-point dictionary is paged: only the `MaxInMemoryNodes`
most-recently-touched nodes are held in RAM at once (a plain LRU), backed by
the same `INodePointStore` passed into the constructor. A node that falls
out of the cache is written to disk; touching it again later reloads its
point list and re-derives the cell keys (deterministic from position + the
node's own bbox/spacing, so nothing extra needs to be persisted) rather than
reusing a stale key it never wrote out. Ancestors near the root are touched
by literally every point, so they stay in memory regardless of the cap;
`MaxInMemoryNodes` really only bounds how many of the (far more numerous)
deep/leaf nodes' point sets can be in memory simultaneously. `Flush()` at the
end just writes out whatever's still in memory — everything evicted mid-run
is already on disk.

This is a straightforward page cache, not a true bounded-memory guarantee
under adversarial access patterns (e.g. input that keeps bouncing between
more distinct deep nodes than fit in the cache would thrash). Real
PotreeConverter 2.0 avoids paging entirely with a two-pass chunked/
external-sort indexer; that's a bigger lift, out of scope here.

Toggle it on in `OctreeLod.App/Program.cs` via the `UseSpacingEngine`
constant at the top of the file.

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
| `UseSpacingEngine` | `Program.cs` | Switches between the legacy split+merge pipeline and the spacing-based single-pass engine. |
| `SpacingIngestionOptions.GridDivisions` | `SpacingIngestionOptions` | Same role as `gridDivisions` above, but for the spacing engine — read directly at both ingest and export time (no separate merge call to keep in sync). |
| `SpacingIngestionOptions.MaxInMemoryNodes` | `SpacingIngestionOptions` | Out-of-core bound: max node point-sets held in RAM at once (LRU-paged to disk). Smaller → less RAM, more disk I/O from evict/reload thrashing on scattered input; larger → more RAM, fewer reloads. |

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
- `leaves.bin` and the `merged/` folder (legacy pipeline) and the `nodes/`
  folder (spacing engine) are scratch space; nothing downstream reads them
  again once the 3D Tiles export has run, so they're safe to delete.
- No crash-recovery checkpointing during a long ingest run (in-memory
  metadata is lost if the process dies mid-run).
- `gridDivisions` is a manually-kept-in-sync literal, not a single shared
  constant — a mismatch between the merge and export calls would silently
  corrupt exported `geometricError` values.
