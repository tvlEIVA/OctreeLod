import {Deck, MapView, COORDINATE_SYSTEM} from '@deck.gl/core';
import {Tile3DLayer} from '@deck.gl/geo-layers';
import {PointCloudLayer} from '@deck.gl/layers';
import {Tiles3DLoader} from '@loaders.gl/3d-tiles';
import {makeBasemapLayer} from './basemap.js';

// Same viewer as main.js, plus two dropdowns (color-by attribute, palette)
// that POST to Meridian's /color-mode — a shared, server-owned setting, so
// this control affects every client watching the same Meridian, not just
// this tab. main.js itself is left untouched; this is a separate page.
const BASE_URL = 'http://localhost:5251';

// Double-buffered tileset: a "front" layer is shown while a "back" layer
// streams a fresh tileset. As soon as the back layer has finished loading it
// becomes the front, the old front is dropped, and a new back starts loading
// right away — no fixed poll period, the refresh rate is whatever one load
// takes. This is the floor on how soon a back layer may be swapped in, so a
// tiny/instant load doesn't spin the server.
const MIN_SWAP_INTERVAL_MS = 1000;
// How often to check whether the back layer has finished loading.
const SWAP_CHECK_MS = 250;

const RECENT_POINTS_POLL_MS = 1000;
const RECENT_POINT_BYTES = 27; // matches OctreeLod.Core PointRecord.ByteSize (3x float64 + 3x uint8)
const RECENT_POINT_SIZE = 4; // vs Tile3DLayer's pointSize=2, to visually flag "not yet in the tree"
// [lng, lat, height] the recent-points meters are offset from. Replaced by Meridian's GET /georeference
// before the first draw, so it matches wherever the tileset root is anchored on the globe.
let recentPointsOrigin = [0, 0, 0];

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

// {id, url, label, startedAt, tileset3d} — front is drawn, back is loading.
let front = null;
let back = null;
let generation = 0;
let recentPointsLayer = null;

const basemapLayer = makeBasemapLayer();

function updateLayers() {
  // Back goes first so, even though it draws nothing, it can never paint over the front.
  const layers = [
    ...(basemapLayer ? [basemapLayer] : []),
    ...(back ? [makeLayer(back, false)] : []),
    ...(front ? [makeLayer(front, true)] : []),
  ];
  if (recentPointsLayer) layers.push(recentPointsLayer);
  deckgl.setProps({layers});
}

// deck.gl only runs tile selection (and so only loads tiles) for layers it
// actually draws — `visible: false` would leave the back tileset empty. So
// the back layer stays "visible" but fully transparent and without depth
// writes, and its points never show or occlude the front.
const BACK_PARAMETERS = {depthWriteEnabled: false};

function makeLayer(slot, isFront) {
  const {id, url, label} = slot;
  return new Tile3DLayer({
    // New id per generation: a repeated id would keep the old tileset loaded,
    // and every swap needs a genuinely fresh one.
    id,
    data: url,
    opacity: isFront ? 1 : 0,
    ...(isFront ? {} : {parameters: BACK_PARAMETERS}),
    loaders: [Tiles3DLoader],
    loadOptions: {
      tileset: {
        maximumScreenSpaceError: 4,
        maxRequests: MAX_CONCURRENT_TILE_REQUESTS,
      },
    },
    pointSize: 2,
    onTilesetLoad: tileset3d => {
      slot.tileset3d = tileset3d;
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
      logMemory(`after load: ${label}`);
    },
    onTileLoad: tile => console.log('[viewer] tile loaded', tile.id),
    onTileError: (tile, tileUrl, message) => {
      console.error('[viewer] tile error', tileUrl, message);
      if (slot === back) statusEl.textContent = `${label} — tile error, see console`;
    },
  });
}

// Replaces the back slot with a freshly requested tileset. Also what the
// color-mode dropdowns call, so a color change doesn't wait for the
// current back layer (loaded with the old colors) to be swapped in first.
// The ?t= cache-buster is a different url per request; the server already
// sends Cache-Control: no-store, this just keeps the url unique.
function restartBack() {
  const now = Date.now();
  const label = `live @ ${new Date(now).toLocaleTimeString()}`;
  back = {id: `tileset-${++generation}`, url: `${BASE_URL}/tileset.json?t=${now}`, label, startedAt: now, tileset3d: null};
  updateLayers();
  statusEl.textContent = front ? `${front.label} — loading ${label}…` : `${label} — loading…`;
}

// The back layer is ready once its tileset3d has nothing left to fetch and
// deck.gl's own layer (sublayers included) reports loaded. Then it swaps to
// front and the next back starts loading immediately.
function swapIfBackLoaded() {
  if (!back || Date.now() - back.startedAt < MIN_SWAP_INTERVAL_MS) return;
  const layer = deckgl.layerManager?.getLayers?.().find(l => l.id === back.id);
  if (!layer?.isLoaded || !back.tileset3d?.isLoaded()) return;

  logMemory(`before swap to: ${back.label}`);
  front = back;
  back = null;
  statusEl.textContent = `${front.label} — loaded`;
  restartBack();
}

function runDoubleBuffer() {
  restartBack();
  setInterval(swapIfBackLoaded, SWAP_CHECK_MS);
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

// ?recentColor=00ffff paints the recent points one flat color, so they stand out
// from the tileset points under them when checking that the two line up.
const recentColorParam = new URLSearchParams(location.search).get('recentColor');
const recentFlatColor = recentColorParam && /^[0-9a-f]{6}$/i.test(recentColorParam)
  ? [0, 2, 4].map(i => parseInt(recentColorParam.slice(i, i + 2), 16))
  : null;

function makeRecentPointsLayer({positions, colors, count}) {
  return new PointCloudLayer({
    id: 'recent-points',
    data: {length: count, attributes: recentFlatColor
      ? {getPosition: {value: positions, size: 3}}
      : {getPosition: {value: positions, size: 3}, getColor: {value: colors, size: 3}}},
    ...(recentFlatColor ? {getColor: recentFlatColor} : {}),
    coordinateSystem: COORDINATE_SYSTEM.METER_OFFSETS,
    coordinateOrigin: recentPointsOrigin,
    pointSize: RECENT_POINT_SIZE,
  });
}

// Reads where Meridian anchors the dataset's local East/North/Up frame, so the
// recent-points layer lands exactly where the tileset does. Falls back to
// the old [0,0,0] origin if Meridian predates /georeference.
async function loadRecentPointsOrigin(baseUrl) {
  try {
    const res = await fetch(`${baseUrl}/georeference`, {cache: 'no-store'});
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const g = await res.json();
    recentPointsOrigin = [g.longitude, g.latitude, g.height];
  } catch (err) {
    console.warn('GET /georeference failed, recent points stay at [0,0,0]:', err);
  }
}

function pollRecentPoints(baseUrl, pollMs) {
  async function tick() {
    const res = await fetch(`${baseUrl}/recent-points.bin`, {cache: 'no-store'});
    recentPointsLayer = makeRecentPointsLayer(decodeRecentPoints(await res.arrayBuffer()));
    updateLayers();
  }
  loadRecentPointsOrigin(baseUrl).then(() => {
    tick();
    setInterval(tick, pollMs);
  });
}

// POSTs the dropdown selections to Meridian's shared color-mode endpoint,
// then restarts the back layer so the change reaches the screen as soon as
// that fresh tileset has loaded. This changes what every
// client sees (server-owned state), not just this tab.
async function applyColorMode() {
  const colorBy = colorByField.value;
  const palette = paletteField.value;
  await fetch(`${BASE_URL}/color-mode?colorBy=${encodeURIComponent(colorBy)}&palette=${encodeURIComponent(palette)}`, {
    method: 'POST',
  });
  restartBack();
}

colorByField.addEventListener('change', applyColorMode);
paletteField.addEventListener('change', applyColorMode);

runDoubleBuffer();
pollRecentPoints(BASE_URL, RECENT_POINTS_POLL_MS);
