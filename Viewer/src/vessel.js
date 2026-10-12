import {COORDINATE_SYSTEM} from '@deck.gl/core';
import {PathLayer, ScatterplotLayer, SolidPolygonLayer} from '@deck.gl/layers';

// Shows where Meridian says the vessel is: GET /vessel gives its position in
// the same local East/North/Up frame as the points (metres from the
// /georeference origin), its roll/pitch/heading and the size of the box to
// draw. The box is drawn as a wireframe with a filled deck and a brighter
// bow face, plus a marker that stays visible when the box is tiny on screen.
const VESSEL_POLL_MS = 500;

const EDGE_COLOR = [255, 255, 255, 255];
const BOW_COLOR = [255, 140, 0, 255];
const DECK_FILL = [255, 255, 255, 70];
const MARKER_COLOR = [255, 140, 0, 255];

let pose = null;

// Calls onChange after every poll that changed what should be drawn.
export function pollVessel(baseUrl, onChange) {
  async function tick() {
    try {
      const res = await fetch(`${baseUrl}/vessel`, {cache: 'no-store'});
      pose = res.status === 200 ? await res.json() : null;
      onChange();
    } catch (err) {
      console.warn('GET /vessel failed:', err);
    }
  }
  tick();
  setInterval(tick, VESSEL_POLL_MS);
}

// Body axes: x forward, y starboard, z down. Rotation is Rz(heading) Ry(pitch) Rx(roll),
// body to north/east/down, the same convention the server derives the angles with.
// Returns a function that maps a body-frame offset to East/North/Up metres.
function bodyToEnu({rollDegrees, pitchDegrees, headingDegrees}) {
  const r = rollDegrees * Math.PI / 180;
  const p = pitchDegrees * Math.PI / 180;
  const h = headingDegrees * Math.PI / 180;
  const [cr, sr, cp, sp, ch, sh] = [Math.cos(r), Math.sin(r), Math.cos(p), Math.sin(p), Math.cos(h), Math.sin(h)];
  const m = [
    [ch * cp, ch * sp * sr - sh * cr, ch * sp * cr + sh * sr],
    [sh * cp, sh * sp * sr + ch * cr, sh * sp * cr - ch * sr],
    [-sp, cp * sr, cp * cr],
  ];
  return (x, y, z) => {
    const north = m[0][0] * x + m[0][1] * y + m[0][2] * z;
    const east = m[1][0] * x + m[1][1] * y + m[1][2] * z;
    const down = m[2][0] * x + m[2][1] * y + m[2][2] * z;
    return [east, north, -down];
  };
}

function boxGeometry(v) {
  const toEnu = bodyToEnu(v);
  const l = v.lengthMeters / 2;
  const w = v.widthMeters / 2;
  const d = v.heightMeters / 2;
  const at = (sx, sy, sz) => {
    const [e, n, u] = toEnu(sx * l, sy * w, sz * d);
    return [v.x + e, v.y + n, v.z + u];
  };

  // sz = -1 is the deck (up), sz = +1 the keel, sx = +1 the bow, sy = +1 starboard.
  const deck = [at(1, -1, -1), at(1, 1, -1), at(-1, 1, -1), at(-1, -1, -1)];
  const keel = [at(1, -1, 1), at(1, 1, 1), at(-1, 1, 1), at(-1, -1, 1)];
  const loop = pts => [...pts, pts[0]];
  const edges = [
    {path: loop(deck), color: EDGE_COLOR},
    {path: loop(keel), color: EDGE_COLOR},
    ...deck.map((corner, i) => ({path: [corner, keel[i]], color: EDGE_COLOR})),
    // Bow face on top of the white edges, so heading reads at a glance.
    {path: loop([deck[0], deck[1], keel[1], keel[0]]), color: BOW_COLOR},
    // Line from the middle out past the bow.
    {path: [at(0, 0, 0), at(2, 0, 0)], color: BOW_COLOR},
  ];
  return {deck, edges, center: [v.x, v.y, v.z]};
}

// Layers for the latest pose, [] before the first one. origin is the
// [lng, lat, height] of the local frame, the same one the recent points use.
export function makeVesselLayers(origin) {
  if (!pose) return [];
  const {deck, edges, center} = boxGeometry(pose);
  const common = {coordinateSystem: COORDINATE_SYSTEM.METER_OFFSETS, coordinateOrigin: origin};

  return [
    new SolidPolygonLayer({
      id: 'vessel-deck',
      data: [{polygon: deck}],
      getPolygon: d => d.polygon,
      getFillColor: DECK_FILL,
      _full3d: true,
      ...common,
    }),
    new PathLayer({
      id: 'vessel-edges',
      data: edges,
      getPath: d => d.path,
      getColor: d => d.color,
      getWidth: 2,
      widthUnits: 'pixels',
      ...common,
    }),
    new ScatterplotLayer({
      id: 'vessel-marker',
      data: [{position: center}],
      getPosition: d => d.position,
      getFillColor: MARKER_COLOR,
      getRadius: 1,
      radiusMinPixels: 6,
      ...common,
    }),
  ];
}
