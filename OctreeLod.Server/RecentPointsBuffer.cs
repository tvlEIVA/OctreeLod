using System;
using System.Collections.Generic;
using OctreeLod.Core.Model;

namespace OctreeLod.Server;

// Fixed-capacity ring buffer of the most recently ingested points,
// independent of the octree entirely — no tree walk, no disk I/O, so a
// client can poll it far more often than /tileset.json. Meant for a
// low-latency "just arrived" overlay in the viewer, bridging the real lag
// between a point being ingested and it becoming visible through the
// normal pipeline (accepted -> Persist()'d to disk -> next tileset.json
// poll -> Tile3DLayer fetching that node's content).
public sealed class RecentPointsBuffer
{
    // ~8MB (PointRecord.ByteSize=27) — under a second of coverage at
    // 350k points/sec. Kept small deliberately: the whole buffer is
    // re-sent, re-decoded, and re-uploaded to the GPU on every poll (see
    // Viewer/src/main.js's RECENT_POINTS_POLL_MS), so this is a direct
    // lever on viewer-side cost, not just "how much history to keep".
    public const int Capacity = 300_000;

    private readonly PointRecord[] _buffer = new PointRecord[Capacity];
    private readonly object _lock = new object();
    private int _writeIndex;
    private int _count;
    private DateTime _lastIngestedAt = DateTime.Now;

    // When the most recent AddBatch call actually ran — i.e. when these
    // points were ingested, not whenever a caller later happens to ask.
    public DateTime LastIngestedAt
    {
        get { lock (_lock) return _lastIngestedAt; }
    }

    // Called once per ingested BATCH (not per point) from the main ingest
    // loop — at ~2,500 points/batch and 350k points/sec that's ~140
    // calls/sec, nowhere near SpacingIngestionEngine.IngestPoint's hot
    // path, so a real lock here costs nothing and buys genuine
    // thread-safety (no torn PointRecord reads) instead of an unforced
    // race.
    public void AddBatch(IReadOnlyList<PointRecord> batch)
    {
        lock (_lock)
        {
            foreach (var p in batch)
            {
                _buffer[_writeIndex] = p;
                _writeIndex = (_writeIndex + 1) % Capacity;
            }
            _count = System.Math.Min(_count + batch.Count, Capacity);
            _lastIngestedAt = DateTime.Now;
        }
    }

    // Oldest-first copy of whatever's currently in the ring — called once
    // per HTTP request, at the viewer's fast poll cadence.
    public PointRecord[] Snapshot()
    {
        lock (_lock)
        {
            var result = new PointRecord[_count];
            int start = _count < Capacity ? 0 : _writeIndex;
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(start + i) % Capacity];
            return result;
        }
    }
}
