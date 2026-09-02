import {Deck, MapView} from '@deck.gl/core';
import {Tile3DLayer} from '@deck.gl/geo-layers';
import {Tiles3DLoader} from '@loaders.gl/3d-tiles';

// Edit these to match your OctreeLod.App run — same "edit constants at the
// top" convention Program.cs uses. BASE_URL must be served over HTTP (a
// file:// URL won't work — fetch/CORS blocks it); simplest is
// `npx serve <tilesDir>` pointed at the workDir/3dtiles folder the console
// output printed, then set BASE_URL to that server's address.
const BASE_URL = 'http://localhost:3000';
const POLL_MS = 5000;

// Overlapping 2 tilesets during a swap (to avoid a blank-frame flicker)
// doubles peak memory right when it's already the tightest — a growing
// live-preview run's tree gets bigger every swap, so peak memory per swap
// climbs over the whole session regardless of this setting. 1 = no overlap,
// no flicker-smoothing, safest against OOM; 2 = smoother swaps, roughly
// doubles peak memory. Drop to 1 first if OOM errors show up again.
const MAX_VISIBLE_TILESETS = 2;

// Tileset3D's own per-instance tile cache cap, in MB (loaders.gl default:
// 32). This only evicts tiles NOT needed by the current view — whatever the
// camera actually has selected can't be evicted, so zooming out far enough
// to need many tiles at once can still exceed this regardless of the cap.
// There's also no sharing between separate Tile3DLayer instances (each
// preview swap is a new one), so this budget applies fresh per swap, not
// cumulatively.
const TILE_CACHE_MB = 64;

// How many pixels of error the traversal tolerates before it descends to a
// finer LOD (loaders.gl default: 8 — Cesium's own default is 16, so this is
// already unusually strict). THIS is what actually bounds how many tiles a
// single view needs loaded at once, not TILE_CACHE_MB above — a large-area
// dataset viewed at a low value here can force selecting a very large tile
// set simultaneously, and that set can't be evicted since it's what's
// currently on screen. Raised well past the earlier 32 — measured logs
// showed the post-GC memory floor climbing every swap even with 2 layers
// bounded and correctly freed, tracking the octree's own growth over a
// long-running preview session; fewer/coarser simultaneous tiles per view
// is the direct lever on that. Trades visual fidelity (blockier LOD) for a
// tighter cap on simultaneous tile count — go higher still if memory is
// still climbing too fast for comfort.
const MAX_SCREEN_SPACE_ERROR = 96;

// Hard cap on simultaneously-selected tiles (loaders.gl default: 0 =
// unlimited). MAX_SCREEN_SPACE_ERROR bounds tile FINENESS but not COUNT —
// zooming in close still shrinks each tile's screen-space error just by
// proximity, so the traversal keeps descending and selecting more/smaller
// tiles regardless of the SSE threshold. This dataset is dense and wide
// enough that "zoom in" alone can drive tile count past what the browser
// can hold or draw smoothly (both the OOM and the lag when zoomed in close
// are this same mechanism). Past this cap, loaders.gl keeps only the N
// tiles closest to the viewport center and simply drops the rest — a
// visible, honest "less detail than requested" instead of a crash or
// stall. Lower this first if either symptom is still happening.
const MAX_TILES_SELECTED = 500;

// Tile SELECTION (which tiles should be visible) is recomputed by walking
// the tree — a cost that scales with tree size — and by default
// (debounceTime: 0) that walk reruns on EVERY frame while the camera is
// moving, competing with rendering for main-thread time during a drag.
// Debouncing so it only reruns this often smooths continuous pan/zoom at
// the cost of tile selection lagging slightly behind the camera by up to
// this many ms (imperceptible at this scale, worth it for smoothness).
const SELECTION_DEBOUNCE_MS = 150;

// Caps how many tiles can finish loading (fetch + parse + GPU upload) in
// the same window (loaders.gl default: 64). Parsing isn't offloaded to a
// worker for this loader, so a burst of completions is a burst of
// synchronous main-thread work — this is the "lag while the loading
// message is showing." Lower = smoother during load, but slower to reach
// full detail after a swap; tune to taste.
const MAX_CONCURRENT_TILE_REQUESTS = 12;

const statusEl = document.getElementById('status');

// MapView (longitude/latitude/zoom/pitch/bearing), not OrbitView. This
// isn't a style choice — Tile3DLayer's point-cloud path is hardcoded to
// treat every tile as geospatial (see _makePointCloudLayer: it always
// projects the tile's local center onto the WGS84 ellipsoid), and
// Tileset3D's own cartographicCenter/zoom are computed specifically to
// drive a MapView-style viewState (longitude/latitude in degrees, zoom on
// the same log2-tile-pyramid scale MapView expects). Pairing OrbitView
// (a "1 unit = 1 pixel" local-scale camera) with those same numbers put
// the camera at an incoherent distance/precision regime — that's why
// zoomed-way-out showed *something*, but it fell apart on any rotation.
let viewState = {longitude: 0, latitude: 0, zoom: 0, pitch: 45, bearing: 0};

// Auto-center only happens once, on the first tileset that ever loads —
// after that the user's own pan/zoom/rotate is left alone. Every later
// preview swap reuses the same longitude/latitude/zoom regardless of how
// the loaded data's extent shifts, since re-centering on every swap would
// yank the camera out from under whatever the user was looking at, every
// ~5s, for the entire run.
let hasCenteredCamera = false;

const deckgl = new Deck({
  parent: document.getElementById('app'), // auto-creates and appends the canvas here
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

// Logs JS heap usage (Chrome-only — performance.memory doesn't exist in
// other browsers) and how many layer instances deck.gl is currently
// tracking (Tile3DLayer + all its currently-mounted per-tile sub-layers,
// across every layer, not just tilesets). Compare consecutive lines:
//   - usedMB jumping at swap time then holding steady after = expected,
//     each new tileset (a bigger tree than the last) needs more memory,
//     and the old one WAS freed — nothing to fix.
//   - usedMB climbing every swap and never coming back down even once the
//     dataset size stabilizes (i.e. after the FINAL tileset.json loads,
//     when no more swaps happen) = a real leak — the old layer's memory
//     isn't being reclaimed.
//   - layerCount growing without bound while MAX_VISIBLE_TILESETS stays
//     fixed = deck.gl is not dropping old layers the way it should; if you
//     see this, that's the concrete proof needed to report a deck.gl bug
//     rather than tune settings further on this end.
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
setInterval(() => logMemory('heartbeat'), 10000); // independent of swap timing, to catch growth between swaps too

// Cheap existence check, no-store so a stale 404 never lingers.
async function urlExists(url) {
  try {
    const res = await fetch(url, {method: 'HEAD', cache: 'no-store'});
    return res.ok;
  } catch {
    return false;
  }
}

// {url, label} entries, oldest first, capped at MAX_VISIBLE_TILESETS — a
// straight single-layer swap left a blank gap between the old layer being
// torn down and the new one finishing its fetch/parse (visible as a
// flicker every ~5s). Keeping the previous tileset mounted alongside the
// new one means it's still rendering, already loaded, while the new one
// streams in; once a third preview arrives the oldest of the two drops.
let activeTilesets = [];
let latestUrl = null;

function makeLayer({url, label}) {
  return new Tile3DLayer({
    // Stable id per URL = deck.gl recognizes this as the SAME layer across
    // repeated setProps calls (as long as url/label haven't changed) and
    // leaves its already-loaded state alone — only a genuinely new url
    // triggers a fresh load. This is what lets two of these coexist in the
    // `layers` array without each other's presence causing reloads.
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

    // Recenter the camera on the ACTUAL loaded data, but only the very
    // first time any tileset resolves — see hasCenteredCamera.
    // Tileset3D computes cartographicCenter/zoom from the real bounding
    // volume, so this is exact, not a guess. cartographicCenter is
    // [longitude, latitude, height] in degrees — exactly MapView's
    // longitude/latitude.
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
      // Only the most-recently-requested tileset drives the status text —
      // an older one (still kept mounted) finishing its load later
      // shouldn't overwrite it.
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

  deckgl.setProps({layers: activeTilesets.map(makeLayer)});
  statusEl.textContent = `${label} — loading…`;
}

// Polls for the next preview / the final export, swapping the displayed
// tileset as soon as something newer is ready. Stops once the final
// tileset.json shows up — that's the last swap that'll ever happen.
function startLivePreview(baseUrl, pollMs) {
  let nextPreview = 1;
  let finalFound = false;
  let timer = null;

  async function tick() {
    if (finalFound) return;

    // Final export always wins if it's there — supersedes every preview.
    const finalUrl = `${baseUrl}/tileset.json`;
    if (await urlExists(finalUrl)) {
      setTileset(finalUrl, 'final tileset.json');
      finalFound = true;
      clearInterval(timer);
      return;
    }

    const n = String(nextPreview).padStart(4, '0');
    const candidate = `${baseUrl}/tileset_preview_${n}.json`;
    if (await urlExists(candidate)) {
      setTileset(candidate, `preview ${n}`);
      nextPreview += 1; // look for the NEXT one on the following tick
    }
    // Neither ready yet — just wait for the next tick.
  }

  tick(); // check immediately, don't wait a full interval for the first one
  timer = setInterval(tick, pollMs);
  return () => clearInterval(timer); // call to stop polling early if needed
}

startLivePreview(BASE_URL, POLL_MS);
