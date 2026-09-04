using System.Collections.Generic;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.Sources;

// Stub: the actual ingestion source (file format, network feed, etc.) is
// caller-supplied and out of scope for this tool.
public interface IPointBatchSource
{
    IEnumerable<IReadOnlyList<PointRecord>> ReadBatches();
}
