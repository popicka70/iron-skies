"""Render neutral turntable views of an aircraft asset for review.

Usage:

    blender --background --python tools/blender/render_turntable.py -- \
        --blend game/assets/aircraft/spitfire-mk9/model/spitfire-mk9.blend \
        --out docs/media/aircraft --prefix spitfire-mk9 --size 640

or import a GLB instead of a .blend:

    blender --background --python tools/blender/render_turntable.py -- \
        --glb game/assets/aircraft/spitfire-mk9/model/spitfire-mk9_lod0.glb
"""

from __future__ import annotations

import argparse
import math
import os
import sys

import bpy
from mathutils import Vector


def parse_args() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--blend")
    parser.add_argument("--glb")
    parser.add_argument("--out", default="docs/media/aircraft")
    parser.add_argument("--prefix", default="aircraft")
    parser.add_argument("--size", type=int, default=640)
    parser.add_argument("--samples", type=int, default=32)
    parser.add_argument("--views", type=int, default=8)
    parser.add_argument("--show-cockpit", action="store_true")
    return parser.parse_args(argv)


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
        if obj.type != "MESH":
            continue
        if any(collection.name.upper().startswith("REFERENCE") for collection in obj.users_collection):
            continue
        meshes.append(obj)
    return meshes


def world_bounds(meshes: list[bpy.types.Object]) -> tuple[Vector, Vector]:
    points = []
    for obj in meshes:
        for corner in obj.bound_box:
            points.append(obj.matrix_world @ Vector(corner))
    low = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    high = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    return low, high


def setup_stage(low: Vector, high: Vector, args: argparse.Namespace) -> bpy.types.Object:
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE_NEXT"
    scene.render.resolution_x = scene.render.resolution_y = args.size
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    scene.render.resolution_percentage = 100
    try:
        scene.eevee.taa_render_samples = args.samples
    except AttributeError:
        pass

    world = bpy.data.worlds.new("StudioWorld")
    world.use_nodes = True
    background = world.node_tree.nodes.get("Background")
    background.inputs["Color"].default_value = (0.035, 0.045, 0.060, 1.0)
    background.inputs["Strength"].default_value = 1.0
    scene.world = world

    center = 0.5 * (low + high)
    span = max(high.x - low.x, high.y - low.y, high.z - low.z)

    # Ground plane, just below the model, for a grounded shadow.
    bpy.ops.mesh.primitive_plane_add(size=span * 8.0, location=(center.x, center.y, low.z - 0.05))
    ground = bpy.context.object
    ground.name = "TurntableGround"
    ground_mat = bpy.data.materials.new("TurntableGround")
    ground_mat.use_nodes = True
    ground_bsdf = ground_mat.node_tree.nodes.get("Principled BSDF")
    ground_bsdf.inputs["Base Color"].default_value = (0.055, 0.060, 0.070, 1.0)
    ground_bsdf.inputs["Roughness"].default_value = 0.85
    ground.data.materials.append(ground_mat)

    # Key, fill and rim lights. Energy scales with span^2 so large aircraft
    # receive the same illuminance as fighters.
    light_scale = (span / 10.0) ** 2
    for name, location, energy, size, color in (
        ("Key", center + Vector((span, -span * 0.8, span * 1.2)), 1800.0 * light_scale, span * 1.2, (1.0, 0.96, 0.90)),
        ("Fill", center + Vector((-span, -span * 0.4, span * 0.6)), 900.0 * light_scale, span * 1.4, (0.75, 0.85, 1.0)),
        ("Rim", center + Vector((0.0, span * 1.2, span * 0.8)), 1400.0 * light_scale, span * 1.0, (0.85, 0.92, 1.0)),
    ):
        light_data = bpy.data.lights.new(name, "AREA")
        light_data.energy = energy
        light_data.shape = "DISK"
        light_data.size = size
        light_data.color = color
        light_obj = bpy.data.objects.new(name, light_data)
        scene.collection.objects.link(light_obj)
        light_obj.location = location
        direction = center - light_obj.location
        light_obj.rotation_mode = "QUATERNION"
        light_obj.rotation_quaternion = direction.to_track_quat("-Z", "Y")

    camera_data = bpy.data.cameras.new("TurntableCamera")
    camera_data.lens = 55.0
    camera = bpy.data.objects.new("TurntableCamera", camera_data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    return ground


def render_views(low: Vector, high: Vector, camera: bpy.types.Object, out_dir: str, prefix: str, args: argparse.Namespace):
    center = 0.5 * (low + high)
    size = max(high.x - low.x, high.y - low.y, high.z - low.z)
    distance = size * 1.55
    for i in range(args.views):
        angle = 2.0 * math.pi * i / args.views + math.radians(35.0)
        elevation = math.radians(14.0)
        position = center + Vector((
            distance * math.cos(angle) * math.cos(elevation),
            distance * math.sin(angle) * math.cos(elevation),
            distance * math.sin(elevation) + size * 0.05,
        ))
        camera.location = position
        direction = center - position
        camera.rotation_mode = "QUATERNION"
        camera.rotation_quaternion = direction.to_track_quat("-Z", "Y")
        path = os.path.join(out_dir, f"{prefix}-view-{i:02d}.png")
        bpy.context.scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        print(f"rendered {path}")


def main(args: argparse.Namespace):
    load_scene(args)
    meshes = visible_meshes()
    if not meshes:
        raise SystemExit("no meshes to render")
    if not args.show_cockpit:
        for collection in bpy.data.collections:
            if collection.name.upper() != "COCKPIT":
                continue
            for obj in collection.objects:
                if obj.type == "MESH":
                    obj.hide_render = True
                    obj.hide_set(True)
        cockpit = bpy.data.objects.get("CockpitInterior")
        if cockpit is not None:
            cockpit.hide_render = True
            cockpit.hide_set(True)
    low, high = world_bounds(meshes)
    setup_stage(low, high, args)
    camera = bpy.data.objects["TurntableCamera"]
    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)
    render_views(low, high, camera, out_dir, args.prefix, args)
    print(f"bounds min={tuple(round(v, 3) for v in low)} max={tuple(round(v, 3) for v in high)}")


if __name__ == "__main__":
    args = parse_args()
    main(args)
