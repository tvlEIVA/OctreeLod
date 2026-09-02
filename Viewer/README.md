# OctreeLod Viewer

deck.gl viewer for OctreeLod's 3D Tiles output. Polls for
`tileset_preview_NNNN.json` files while a `OctreeLod.App` run is still
ingesting, swapping to each newer one as it appears, then swaps to the final
`tileset.json` and stops polling once that's written.

## Setup

```bash
npm install
```

## Serve the exported tiles

`OctreeLod.App`'s console output prints the output folder, e.g.
`D:\tmp\OctreeLodDemo-<guid>\3dtiles`. Serve that folder over HTTP (plain
`file://` won't work — the browser blocks the fetches this viewer makes):

```bash
npx serve "D:\tmp\OctreeLodDemo-<guid>\3dtiles" -l 3000
```

Then edit `BASE_URL` in `src/main.js` if it isn't `http://localhost:3000`,
and `VIEW_TARGET` to roughly your dataset's center in local meters (e.g.
`SyntheticAreaSize / 2` for the synthetic waving-surface source).

## Run

```bash
npm run dev
```

Open the printed local URL (or use the "dev server" task in VS Code —
Terminal → Run Task). You can start this before `OctreeLod.App` finishes
ingesting; it'll pick up new previews as they land and swap to the final
export automatically once ingestion completes.
