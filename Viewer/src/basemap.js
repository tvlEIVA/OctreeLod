import {TileLayer} from '@deck.gl/geo-layers';
import {BitmapLayer} from '@deck.gl/layers';

// Background map under the point cloud, to check the dataset sits where it
// should. Pick one with ?basemap=osm (default), ?basemap=satellite, or
// ?basemap=off. Raster tiles come straight from the public servers below, so
// they need internet and are meant for local checking, not for production use.
const SOURCES = {
  osm: {
    url: 'https://tile.openstreetmap.org/{z}/{x}/{y}.png',
    attribution: '© OpenStreetMap contributors',
    maxZoom: 19,
  },
  satellite: {
    url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
    attribution: 'Imagery © Esri, Maxar, Earthstar Geographics',
    maxZoom: 19,
  },
};

// The map sits at sea level while the seabed points are below it, so it must
// neither hide them (no depth test, no depth write) nor draw over them (it is
// the first layer, so everything else paints after it).
const UNDERLAY = {depthCompare: 'always', depthWriteEnabled: false};

export function makeBasemapLayer() {
  const choice = new URLSearchParams(location.search).get('basemap') ?? 'osm';
  const source = SOURCES[choice];
  if (!source) return null;

  const credit = document.createElement('div');
  credit.textContent = source.attribution;
  credit.style.cssText =
    'position:absolute;right:8px;bottom:8px;z-index:1;font:11px/1.4 sans-serif;color:#eee;' +
    'background:rgba(0,0,0,0.5);padding:2px 6px;border-radius:3px;pointer-events:none;';
  document.body.appendChild(credit);

  return new TileLayer({
    id: 'basemap',
    data: source.url,
    minZoom: 0,
    maxZoom: source.maxZoom,
    tileSize: 256,
    pickable: false,
    parameters: UNDERLAY,
    renderSubLayers: props => {
      const {west, south, east, north} = props.tile.bbox;
      return new BitmapLayer(props, {
        data: null,
        image: props.data,
        bounds: [west, south, east, north],
        parameters: UNDERLAY,
      });
    },
  });
}
