import {Deck, MapView, COORDINATE_SYSTEM} from '@deck.gl/core';
import {Tile3DLayer} from '@deck.gl/geo-layers';
import {PointCloudLayer} from '@deck.gl/layers';
import {Tiles3DLoader} from '@loaders.gl/3d-tiles';

// Same viewer as main.js, plus two dropdowns (color-by attribute, palette)
// that POST to Meridian's /color-mode — a shared, server-owned setting, so
// this control affects every client watching the same Meridian, not just
// this tab. main.js itself is left untouched; this is a separate page.
const BASE_URL = 'http://localhost:5251';
const POLL_MS = 10000;

const RECENT_POINTS_POLL_MS = 1000;
const RECENT_POINT_BYTES = 27; // matches OctreeLod.Core PointRecord.ByteSize (3x float64 + 3x uint8)
const RECENT_POINT_SIZE = 4; // vs Tile3DLayer's pointSize=2, to visually flag "not yet in the tree"
const RECENT_POINTS_ORIGIN = [0, 0, 0]; // must match Program.cs's hardcoded synthetic GeoReference

const MAX_VISIBLE_TILESETS = 2;
const TILE_CACHE_MB = 32;
const MAX_SCREEN_SPACE_ERROR = 8;
const MAX_TILES_SELECTED = 800;
const SELECTION_DEBOUNCE_MS = 150;
const MAX_CONCURRENT_TILE_REQUESTS = 12;

const statusEl = document.getElementById('status');
const colorByField = document.getElementById('colorByField');
const paletteField = document.getElementById('paletteField');

let viewState = {longitude: 0, latitude: 0, zoom: 0, pitch: 45, bearing: 0};
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

let activeTilesets = [];
let latestUrl = null;
let recentPointsLayer = null;

function updateLayers() {
  const layers = activeTilesets.map(makeLayer);
  if (recentPointsLayer) layers.push(recentPointsLayer);
  deckgl.setProps({layers});
}

function makeLayer({url, label}) {
  return new Tile3DLayer({
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

// Exposed (not just interval-scheduled) so the color-mode dropdowns can
// force an immediate refetch instead of waiting up to POLL_MS for the
// next scheduled tick.
let tickTileset = () => {};

function pollLiveServer(baseUrl, pollMs) {
  tickTileset = () => {
    const url = `${baseUrl}/tileset.json?t=${Date.now()}`;
    setTileset(url, `live @ ${new Date().toLocaleTimeString()}`);
  };
  tickTileset();
  setInterval(tickTileset, pollMs);
}

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
    id: 'recent-points',
    data: {length: count, attributes: {getPosition: {value: positions, size: 3}, getColor: {value: colors, size: 3}}},
    coordinateSystem: COORDINATE_SYSTEM.METER_OFFSETS,
    coordinateOrigin: RECENT_POINTS_ORIGIN,
    pointSize: RECENT_POINT_SIZE,
  });
}

function pollRecentPoints(baseUrl, pollMs) {
  async function tick() {
    const res = await fetch(`${baseUrl}/recent-points.bin`, {cache: 'no-store'});
    recentPointsLayer = makeRecentPointsLayer(decodeRecentPoints(await res.arrayBuffer()));
    updateLayers();
  }
  tick();
  setInterval(tick, pollMs);
}

// POSTs the dropdown selections to Meridian's shared color-mode endpoint,
// then forces an immediate tileset refetch so the change is visible right
// away instead of waiting for the next poll tick. This changes what every
// client sees (server-owned state), not just this tab.
async function applyColorMode() {
  const colorBy = colorByField.value;
  const palette = paletteField.value;
  await fetch(`${BASE_URL}/color-mode?colorBy=${encodeURIComponent(colorBy)}&palette=${encodeURIComponent(palette)}`, {
    method: 'POST',
  });
  tickTileset();
}

colorByField.addEventListener('change', applyColorMode);
paletteField.addEventListener('change', applyColorMode);

pollLiveServer(BASE_URL, POLL_MS);
pollRecentPoints(BASE_URL, RECENT_POINTS_POLL_MS);
