using System;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.Ingest;

public sealed class OctreeIngestionOptions
{
    // Fixed, generous world-scale root — chosen over a dynamically-growing
    // root (see design notes) to avoid the reparenting/boundary edge cases
    // that approach needed several corrections for. Every real point fits
    // from the start; the one-time cost of descending from world scale down
    // to where data actually clusters is cheap and gets cleaned up by
    // AdaptiveRootTrimmer for the emitted structure.
    public BoundingCube WorldBounds { get; set; } =
        new BoundingCube(-10_000_000, -10_000_000, -10_000_000, 20_000_000);

    public int SplitThreshold { get; set; } = 1000;

    // Covers baseline world->site descent plus real depth from point
    // density. Only hit by pathological (near-)duplicate point clusters
    // that spatial splitting can never separate.
    public int MaxSplitDepth { get; set; } = 60;

    public Action<string>? OnWarning { get; set; }
}
