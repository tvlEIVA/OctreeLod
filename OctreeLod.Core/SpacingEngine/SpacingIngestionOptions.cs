using System;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.SpacingEngine;

public sealed class SpacingIngestionOptions
{
    // Same fixed, generous world-scale root as OctreeIngestionOptions — see
    // that type's design notes. AdaptiveRootTrimmer cleans up the resulting
    // wrapper levels for the emitted structure.
    public BoundingCube WorldBounds { get; set; } =
        new BoundingCube(-10_000_000, -10_000_000, -10_000_000, 20_000_000);

    // Cells per bbox edge when computing a node's spacing threshold
    // (cellSize = node.Bbox.Size / GridDivisions). Halves automatically each
    // level since child bbox is always half the parent's. Same constant and
    // formula as MergeEngine/GridSubsampler use, kept in sync so output from
    // both engines is directly comparable for the same input.
    public int GridDivisions { get; set; } = 64;

    // Guards against a (near-)duplicate point cluster that spatial
    // splitting can never separate — same role as
    // OctreeIngestionOptions.MaxSplitDepth.
    public int MaxSplitDepth { get; set; } = 60;

    // Out-of-core bound: max number of nodes whose accepted-point set is
    // held in RAM at once (LRU). Every other node's data lives on disk in
    // the INodePointStore passed to SpacingIngestionEngine and is reloaded
    // on next touch. Ancestors near the root are touched by every point and
    // so stay resident regardless of this value; this bounds how many
    // deep/leaf nodes' point sets can be resident simultaneously.
    public int MaxResidentNodes { get; set; } = 4096;

    public Action<string>? OnWarning { get; set; }
}
