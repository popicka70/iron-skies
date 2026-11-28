"""Export authored LOD1/LOD2 meshes from the aircraft source scenes.

LOD0 is the shipped `_lod0.glb`. LOD1 (≈45% of the triangles) and LOD2 (≈16%)
drop the cockpit interior and are exported **without materials**: the runtime
reuses the LOD0 materials, so the 4K normal/AO/ORM maps are not duplicated in
every LOD bundle.

    blender --background --python tools/blender/build_lods.py
"""

from __future__ import annotations

import argparse
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from aircraft_common import export_glb  # noqa: E402

AIRCRAFT = ("spitfire-mk9", "bf109-g6", "ju88-a4", "p51d-25")
RATIOS = {1: 0.45, 2: 0.16}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--aircraft", action="append", default=[])
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])

    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    selected = args.aircraft or list(AIRCRAFT)
    for aircraft in selected:
        blend = os.path.join(root, "art", "aircraft", aircraft, f"{aircraft}.blend")
        for level, ratio in RATIOS.items():
            bpy.ops.wm.open_mainfile(filepath=blend)
            for obj in list(bpy.data.objects):
                if obj.type == "MESH" and any(
                        collection.name.upper() == "COCKPIT"
                        for collection in obj.users_collection):
                    bpy.data.objects.remove(obj, do_unlink=True)
            cockpit_root = bpy.data.objects.get("CockpitInterior")
            if cockpit_root is not None:
                bpy.data.objects.remove(cockpit_root, do_unlink=True)

            meshes = [obj for obj in bpy.data.objects if obj.type == "MESH"]
            for obj in meshes:
                modifier = obj.modifiers.new("LodDecimate", "DECIMATE")
                modifier.ratio = ratio
                modifier.use_collapse_triangulate = True

            out = os.path.join(
                root, "game", "assets", "aircraft", aircraft, "model",
                f"{aircraft}_lod{level}.glb")
            export_glb(out, [obj for obj in bpy.data.objects
                             if obj.type in ("MESH", "EMPTY")],
                       materials="NONE")
            print(f"wrote {out} ({ratio:.0%} decimate)")


if __name__ == "__main__":
    main()
