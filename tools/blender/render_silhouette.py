"""Render orthographic side/top/front silhouettes for reference overlays.

The review compares the model against a reference 3-view at a known scale. Each
view is rendered with a flat single colour and a transparent background on an
orthographic camera, and the script writes a JSON sidecar with the metres-to-
pixels mapping so an overlay can be composed without guessing.

Usage:

    blender --background --python tools/blender/render_silhouette.py -- \
        --blend art/aircraft/spitfire-mk9/spitfire-mk9.blend \
        --out /tmp/art-review --prefix spitfire-mk9 --ppm 90

    blender --background --python tools/blender/render_silhouette.py -- \
        --glb game/assets/aircraft/spitfire-mk9/model/spitfire-mk9_lod0.glb
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys

import bpy
from mathutils import Vector

VIEWS = ("side", "top", "front")


def parse_args() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--blend")
    parser.add_argument("--glb")
    parser.add_argument("--out", default="/tmp/art-review")
    parser.add_argument("--prefix", default="aircraft")
    parser.add_argument("--ppm", type=float, default=90.0,
                        help="target pixels per metre")
    parser.add_argument("--margin", type=float, default=0.06,
                        help="fractional margin around the bounds")
    parser.add_argument("--views", default="side,top,front")
    parser.add_argument("--show-gear", action="store_true",
                        help="keep the landing gear (hidden by default: the "
                             "reference 3-views show the aircraft in flight)")
    return parser.parse_args(argv)


def hide_gear() -> None:
    """Hide the gear hierarchy so the silhouette matches an in-flight view."""
    roots = [bpy.data.objects.get("GearMainL"),
             bpy.data.objects.get("GearMainR"),
             bpy.data.objects.get("GearTail")]
    pending = [root for root in roots if root is not None]
    while pending:
        obj = pending.pop()
        pending.extend(obj.children)
        if obj.type == "MESH":
            obj.hide_render = True
            obj.hide_set(True)
    for obj in bpy.context.scene.objects:
        if obj.type == "MESH" and obj.name.startswith("Gear"):
            obj.hide_render = True
            obj.hide_set(True)


def load_scene(args: argparse.Namespace) -> None:
    if args.blend:
        bpy.ops.wm.open_mainfile(filepath=os.path.abspath(args.blend))
    elif args.glb:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.gltf(filepath=os.path.abspath(args.glb))
    else:
        raise SystemExit("pass --blend or --glb")


def visible_meshes() -> list[bpy.types.Object]:
    meshes = []
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH" or obj.hide_render:
            continue
        if any(collection.name.upper().startswith("REFERENCE")
               for collection in obj.users_collection):
            continue
        if obj.name.startswith("CockpitInterior") or obj.name.startswith("Cockpit"):
            continue
        if any(p.name.upper().startswith("COCKPIT") for p in obj.users_collection):
            continue
        meshes.append(obj)
    return meshes


def world_bounds(meshes: list[bpy.types.Object]) -> tuple[Vector, Vector]:
    points = []
    for obj in meshes:
        for corner in obj.bound_box:
            points.append(obj.matrix_world @ Vector(corner))
    low = Vector((min(p.x for p in points), min(p.y for p in points),
                  min(p.z for p in points)))
    high = Vector((max(p.x for p in points), max(p.y for p in points),
                   max(p.z for p in points)))
    return low, high


def setup_render() -> None:
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.film_transparent = True
    scene.render.resolution_percentage = 100
    shading = scene.display.shading
    shading.light = "FLAT"
    shading.color_type = "SINGLE"
    shading.single_color = (0.10, 0.11, 0.13)
    shading.show_cavity = False
    shading.show_shadows = False


def make_camera(view: str, center: Vector, extent: float) -> bpy.types.Object:
    camera_data = bpy.data.cameras.new(f"SilhouetteCamera-{view}")
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = extent
    camera = bpy.data.objects.new(f"SilhouetteCamera-{view}", camera_data)
    bpy.context.scene.collection.objects.link(camera)
    distance = max(extent * 4.0, 10.0)
    offset = {
        "side": Vector((distance, 0.0, 0.0)),
        "top": Vector((0.0, 0.0, distance)),
        "front": Vector((0.0, distance, 0.0)),
    }[view]
    camera.location = center + offset
    direction = center - camera.location
    camera.rotation_mode = "QUATERNION"
    camera.rotation_quaternion = direction.to_track_quat("-Z", "Y")
    bpy.context.scene.camera = camera
    return camera


def axis_extents(view: str, low: Vector, high: Vector) -> tuple[float, float]:
    # (horizontal, vertical) extents in the rendered image.
    if view == "side":
        return high.y - low.y, high.z - low.z
    if view == "top":
        return high.x - low.x, high.y - low.y
    return high.x - low.x, high.z - low.z


def main(args: argparse.Namespace) -> None:
    load_scene(args)
    if not args.show_gear:
        hide_gear()
    meshes = visible_meshes()
    if not meshes:
        raise SystemExit("no exterior meshes to render")
    low, high = world_bounds(meshes)
    center = 0.5 * (low + high)
    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)
    setup_render()

    record = {
        "blend": args.blend,
        "glb": args.glb,
        "bounds": {
            "min": [round(v, 4) for v in low],
            "max": [round(v, 4) for v in high],
        },
        "size_m": {
            "span": round(high.x - low.x, 4),
            "length": round(high.y - low.y, 4),
            "height": round(high.z - low.z, 4),
        },
        "views": {},
    }

    for view in args.views.split(","):
        view = view.strip()
        if view not in VIEWS:
            raise SystemExit(f"unknown view {view!r}")
        horizontal, vertical = axis_extents(view, low, high)
        span = max(horizontal, vertical) * (1.0 + args.margin)
        size = max(64, int(math.ceil(span * args.ppm)))
        scene = bpy.context.scene
        scene.render.resolution_x = scene.render.resolution_y = size
        make_camera(view, center, span)
        path = os.path.join(out_dir, f"{args.prefix}-{view}.png")
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        record["views"][view] = {
            "file": path,
            "pixels": size,
            "ortho_scale_m": round(span, 4),
            "pixels_per_metre": round(size / span, 4),
            "center": [round(v, 4) for v in center],
        }
        print(f"rendered {path} ({size}², {size / span:.2f} px/m)")

    record_path = os.path.join(out_dir, f"{args.prefix}-silhouette.json")
    with open(record_path, "w", encoding="utf-8") as handle:
        json.dump(record, handle, indent=2)
        handle.write("\n")
    print(f"bounds min={tuple(round(v, 3) for v in low)} "
          f"max={tuple(round(v, 3) for v in high)}")
    print(f"span={record['size_m']['span']:.3f} m "
          f"length={record['size_m']['length']:.3f} m "
          f"height={record['size_m']['height']:.3f} m")
    print(f"wrote {record_path}")


if __name__ == "__main__":
    main(parse_args())
