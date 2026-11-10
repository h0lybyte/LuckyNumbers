"""Light masks for the PSX kits' atlases: atlas.png gets an alpha channel, 1 where the kit painted a light
(tube, bulb, strip), 0 elsewhere. The StationKit shader uses it to dim the lights (off) or turn them red
(emergency) without touching the rest of the wall. RGB stays exactly as the kit shipped it.

Then both are packed into one atlas (Art/Models/CombinedKit/atlas.png), so the whole station draws with one
material: the starship kit's band (rows 0-288) stays where it is, the station kit's (rows 0-320) moves down to
rows 320-640. StationView shifts station-kit UVs to match (StationView.StationAtlasOffset = 320 / 2048).

    python3 tools/kitmask.py            # rewrite both atlases' alpha from the regions below
    python3 tools/kitmask.py --preview  # also write Logs/kitmask-<kit>.png (mask in magenta over the atlas)

Needs Pillow. Regions are atlas pixel rects (x0, y0, x1, y1), top-left origin, plus the brightness (0..1,
max of RGB) a texel needs inside the rect to count as light, so panel texels around a tube stay out.
"""
from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
MODELS = ROOT / "LuckyNumbers" / "Assets" / "_Project" / "Art" / "Models"

REGIONS = {
    "StarshipKit": [
        ((1056, 30, 1184, 58), 0.80),   # slant_lights: the warm strip in the corridor wall's lean
        ((1600, 0, 1728, 32), 0.70),    # ceiling_corridor: the segmented strip down its middle
        ((1920, 0, 1936, 8), 0.60),     # light_red: the caged lamp
    ],
    "StationKit": [
        ((1024, 0, 1152, 128), 0.80),   # tech_lights: the white tubes in wall_station_lights
        ((1408, 0, 1536, 32), 0.75),    # trim_lights: the row of bulbs
    ],
}


def mask(kit: str, preview: bool) -> None:
    path = MODELS / kit / "atlas.png"
    im = Image.open(path).convert("RGBA")
    rgb = im.convert("RGB")
    alpha = Image.new("L", im.size, 0)
    px, out = rgb.load(), alpha.load()
    count = 0
    for (x0, y0, x1, y1), threshold in REGIONS[kit]:
        for y in range(y0, y1):
            for x in range(x0, x1):
                if max(px[x, y]) / 255.0 >= threshold:
                    out[x, y] = 255
                    count += 1
    rgb.putalpha(alpha)
    rgb.save(path, optimize=True)
    print(f"  {kit}: {count} light texels -> {path.relative_to(ROOT)}")
    if preview:
        view = Image.open(path).convert("RGB")
        w = max(r[0][2] for r in REGIONS[kit]) + 64
        view = view.crop((0, 0, min(w, view.width), 320))
        a = alpha.crop((0, 0, view.width, 320))
        magenta = Image.new("RGB", view.size, (255, 0, 255))
        view = Image.composite(magenta, view, a)
        out_path = ROOT / "LuckyNumbers" / "Logs" / f"kitmask-{kit}.png"
        view.save(out_path)
        print(f"  preview -> {out_path.relative_to(ROOT)}")


STATION_BAND = 320   # rows of the station kit's atlas in use; also its offset in the combined atlas


def combine() -> None:
    ship = Image.open(MODELS / "StarshipKit" / "atlas.png").convert("RGBA")
    station = Image.open(MODELS / "StationKit" / "atlas.png").convert("RGBA")
    assert ship.size == station.size == (2048, 2048), "kit atlases are 2048 x 2048"
    assert ship.getbbox() is None or ship.getchannel("R").getbbox()[3] <= STATION_BAND, "starship band overflows"
    out = Image.new("RGBA", ship.size, (0, 0, 0, 0))
    out.paste(ship.crop((0, 0, 2048, STATION_BAND)), (0, 0))
    out.paste(station.crop((0, 0, 2048, STATION_BAND)), (0, STATION_BAND))
    path = MODELS / "CombinedKit" / "atlas.png"
    path.parent.mkdir(parents=True, exist_ok=True)
    out.save(path, optimize=True)
    print(f"  combined -> {path.relative_to(ROOT)}")


if __name__ == "__main__":
    for kit in REGIONS:
        mask(kit, "--preview" in sys.argv)
    combine()
