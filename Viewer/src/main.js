import {Deck, MapView, COORDINATE_SYSTEM} from '@deck.gl/core';
import {Tile3DLayer} from '@deck.gl/geo-layers';
import {PointCloudLayer} from '@deck.gl/layers';
import {Tiles3DLoader} from '@loaders.gl/3d-tiles';

// Point this at a running OctreeLod.Server (see OctreeLod.Server/Program.cs).
const BASE_URL = 'http://localhost:5251';
const POLL_MS = 5000;

// /recent-points.bin is a fixed-size in-memory buffer, no tree walk/disk
// I/O — cheap enough to poll faster than the tileset above, bridging the
// real lag between a point being ingested and it surviving the full
// pipeline (accepted -> persisted -> next tileset poll -> tile fetch). The
// whole buffer is re-sent/re-decoded/re-uploaded every tick (no delta), so
// this interval directly trades viewer cost against freshness — raise it
// if the viewer lags, lower it for fresher recent points.
const RECENT_POINTS_POLL_MS = 500;
const RECENT_POINT_BYTES = 27; // matches OctreeLod.Core PointRecord.ByteSize (3x float64 + 3x uint8)
const RECENT_POINT_SIZE = 4; // vs Tile3DLayer's pointSize=2, to visually flag "not yet in the tree"
const RECENT_POINTS_ORIGIN = [0, 0, 0]; // must match Program.cs's hardcoded synthetic GeoReference

// Keep 2 tilesets mounted during a swap so the old one keeps rendering while
// the new one streams in (avoids a blank-frame flicker). Costs roughly 2x
// peak memory per swap; drop to 1 if that's a problem.
const MAX_VISIBLE_TILESETS = 2;

// Tileset3D's own tile cache cap, in MB (loaders.gl default: 32). Only evicts
// tiles not needed by the current view, so it's not a hard ceiling.
const TILE_CACHE_MB = 32;

// Pixels of screen-space error tolerated before descending to a finer LOD
// (loaders.gl default: 8). Higher = coarser but fewer simultaneous tiles.
const MAX_SCREEN_SPACE_ERROR = 8;

// Hard cap on simultaneously-selected tiles (loaders.gl default: unlimited).
// Past this, loaders.gl keeps only the tiles closest to the viewport center.
const MAX_TILES_SELECTED = 800;

// Debounce tile selection (which recomputes by walking the tree) so it
// doesn't rerun every frame during a camera drag.
const SELECTION_DEBOUNCE_MS = 150;

// Cap on tiles finishing load (fetch+parse+GPU upload) per window
// (loaders.gl default: 64) — parsing is synchronous, so a burst is main-
// thread jank. Lower = smoother loading, slower to reach full detail.
const MAX_CONCURRENT_TILE_REQUESTS = 12;

const statusEl = document.getElementById('status');

// MapView, not OrbitView: Tile3DLayer's point-cloud path always projects
// tiles onto the WGS84 ellipsoid, so the camera needs matching geospatial
// (lon/lat/zoom) semantics.
let viewState = {longitude: 0, latitude: 0, zoom: 0, pitch: 45, bearing: 0};

// Camera auto-centers once, on the first tileset load, then leaves the
// user's own pan/zoom/rotate alone on later swaps.
let hasCenteredCamera = false;

const deckgl = new Deck({
  parent: document.getElementById('app'),
  width: '100%',
  height: '100%',
  views: new MapView({repeat: true}),
  viewState,
  controller: true,
  onViewStateChange: ({viewState: nextViewState}) => {
    viewState = nextViewState;
    deckgl.setProps({viewState});
  },
  layers: [],
});

// Chrome-only (performance.memory). Watch for usedMB climbing forever across
// swaps (leak) or layerCount growing past MAX_VISIBLE_TILESETS (deck.gl not
// releasing old layers).
function logMemory(label) {
  const mem = performance.memory;
  const layerCount = deckgl.layerManager?.getLayers?.().length ?? 'n/a';
  if (mem) {
    console.log(
      `[viewer:mem] ${label} usedMB=${(mem.usedJSHeapSize / 1048576).toFixed(1)} ` +
        `totalMB=${(mem.totalJSHeapSize / 1048576).toFixed(1)} layers=${layerCount}`
    );
  } else {
    console.log(`[viewer:mem] ${label} layers=${layerCount} (performance.memory unavailable — use Chrome)`);
  }
}
setInterval(() => logMemory('heartbeat'), 10000);

// {url, label} entries, oldest first, capped at MAX_VISIBLE_TILESETS.
let activeTilesets = [];
let latestUrl = null;

// Set by pollRecentPoints; composed into the same layers array as the
// tile layers by updateLayers, so neither poll loop clobbers the other's
// contribution.
let recentPointsLayer = null;

function updateLayers() {
  const layers = activeTilesets.map(makeLayer);
  if (recentPointsLayer) layers.push(recentPointsLayer);
  deckgl.setProps({layers});
}

function makeLayer({url, label}) {
  return new Tile3DLayer({
    // Stable id per url — deck.gl treats a repeated url as the same layer
    // and leaves its loaded state alone; only a new url triggers a reload.
    id: `tileset-${url}`,
    data: url,
    loaders: [Tiles3DLoader],
    loadOptions: {
      tileset: {
        maximumMemoryUsage: TILE_CACHE_MB,
        maximumScreenSpaceError: MAX_SCREEN_SPACE_ERROR,
        maximumTilesSelected: MAX_TILES_SELECTED,
        debounceTime: SELECTION_DEBOUNCE_MS,
        throttleRequests: true,
        maxRequests: MAX_CONCURRENT_TILE_REQUESTS,
      },
    },
    pointSize: 2,
    onTilesetLoad: tileset3d => {
      const {cartographicCenter, zoom} = tileset3d;
      console.log('[viewer] tileset loaded', url, {cartographicCenter, zoom});
      if (cartographicCenter && !hasCenteredCamera) {
        hasCenteredCamera = true;
        viewState = {
          ...viewState,
          longitude: cartographicCenter[0],
          latitude: cartographicCenter[1],
          zoom: Number.isFinite(zoom) ? zoom : viewState.zoom,
        };
        deckgl.setProps({viewState});
      }
      if (url === latestUrl) statusEl.textContent = `${label} — loaded`;
      logMemory(`after load: ${label}`);
    },
    onTileLoad: tile => console.log('[viewer] tile loaded', tile.id),
    onTileError: (tile, tileUrl, message) => {
      console.error('[viewer] tile error', tileUrl, message);
      if (url === latestUrl) statusEl.textContent = `${label} — tile error, see console`;
    },
  });
}

function setTileset(url, label) {
  logMemory(`before swap to: ${label}`);
  latestUrl = url;
  activeTilesets.push({url, label});
  if (activeTilesets.length > MAX_VISIBLE_TILESETS) activeTilesets.shift();

  updateLayers();
  statusEl.textContent = `${label} — loading…`;
}

// OctreeLod.Server's /tileset.json is the same url forever, regenerated
// fresh from the live tree on every request — no "final" state to detect,
// so this just polls forever. The cache-busting query param forces a real
// refetch: the server already sends Cache-Control: no-store, but
// Tile3DLayer keys its own "already loaded" state off the exact url string
// (see makeLayer's id), so an unchanged url would otherwise be skipped.
function pollLiveServer(baseUrl, pollMs) {
  function tick() {
    const url = `${baseUrl}/tileset.json?t=${Date.now()}`;
    setTileset(url, `live @ ${new Date().toLocaleTimeString()}`);
  }
  tick();
  setInterval(tick, pollMs);
}

// Reads float64 X/Y/Z off the wire (matches the server's `double` layout)
// but stores them as Float32Array for the GPU attribute — safe since these
// are meters offset from RECENT_POINTS_ORIGIN, bounded by the synthetic
// area size, so float32's precision loss is well under a millimeter.
function decodeRecentPoints(buffer) {
  const count = buffer.byteLength / RECENT_POINT_BYTES;
  const view = new DataView(buffer);
  const positions = new Float32Array(count * 3);
  const colors = new Uint8Array(count * 3);
  let o = 0;
  for (let i = 0; i < count; i++) {
    positions[i * 3] = view.getFloat64(o, true); o += 8;
    positions[i * 3 + 1] = view.getFloat64(o, true); o += 8;
    positions[i * 3 + 2] = view.getFloat64(o, true); o += 8;
    colors[i * 3] = view.getUint8(o++);
    colors[i * 3 + 1] = view.getUint8(o++);
    colors[i * 3 + 2] = view.getUint8(o++);
  }
  return {positions, colors, count};
}

function makeRecentPointsLayer({positions, colors, count}) {
  return new PointCloudLayer({
    id: 'recent-points', // stable id — updates in place instead of reallocating GPU buffers each tick
    data: {length: count, attributes: {getPosition: {value: positions, size: 3}, getColor: {value: colors, size: 3}}},
    coordinateSystem: COORDINATE_SYSTEM.METER_OFFSETS,
    coordinateOrigin: RECENT_POINTS_ORIGIN,
    pointSize: RECENT_POINT_SIZE,
  });
}

// No LOD needed — this is a flat, always-fully-visible buffer, not a
// hierarchy, so no Tile3DLayer/tileset machinery involved: one fetch, one
// GPU upload, straight to PointCloudLayer.
function pollRecentPoints(baseUrl, pollMs) {
  async function tick() {
    const res = await fetch(`${baseUrl}/recent-points.bin`, {cache: 'no-store'});
    recentPointsLayer = makeRecentPointsLayer(decodeRecentPoints(await res.arrayBuffer()));
    updateLayers();
  }
  tick();
  setInterval(tick, pollMs);
}

pollLiveServer(BASE_URL, POLL_MS);
pollRecentPoints(BASE_URL, RECENT_POINTS_POLL_MS);
