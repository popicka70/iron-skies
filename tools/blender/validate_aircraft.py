"""Validate an exported aircraft GLB against the project contract.

Usage:

    blender --background --python tools/blender/validate_aircraft.py -- \
        --glb game/assets/aircraft/spitfire-mk9/model/spitfire-mk9_lod0.glb \
        --id spitfire-mk9 --span 11.23 --length 9.47

Writes <glb>.gltf_meta.json next to the asset and exits non-zero on a hard failure.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys

import bpy
from mathutils import Vector

REQUIRED_NODES = {
    "spitfire-mk9": [
        "Propeller", "AileronL", "AileronR", "FlapL", "FlapR",
        "ElevatorL", "ElevatorR", "Rudder",
        "GearMainL", "GearMainR", "GearTail",
        "Canopy", "CockpitInterior", "DamageSmokeAnchor", "FuselageCenter",
    ],
    "bf109-g6": [
        "Propeller", "AileronL", "AileronR", "FlapL", "FlapR",
        "ElevatorL", "ElevatorR", "Rudder",
        "GearMainL", "GearMainR", "GearTail",
        "Canopy", "CockpitInterior", "DamageSmokeAnchor", "FuselageCenter",
    ],
    "ju88-a4": [
        "PropellerL", "PropellerR",
        "AileronL", "AileronR", "FlapL", "FlapR",
        "ElevatorL", "ElevatorR", "Rudder",
        "GearMainL", "GearMainR", "GearTail",
        "DamageSmokeAnchor",
    ],
}


def parse_args() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--glb", required=True)
    parser.add_argument("--id", required=True)
    parser.add_argument("--span", type=float, required=True)
    parser.add_argument("--length", type=float, required=True)
    parser.add_argument("--max-tris", type=int, default=60000)
    parser.add_argument("--tolerance", type=float, default=0.03)
    parser.add_argument("--out")
    return parser.parse_args(argv)


def main(args: argparse.Namespace) -> int:
    glb = os.path.abspath(args.glb)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=glb)

    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    if not meshes:
        print("FAIL: no meshes imported")
        return 1

    points = []
    for obj in meshes:
        for corner in obj.bound_box:
            points.append(obj.matrix_world @ Vector(corner))
    low = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    high = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    size = high - low

    triangles = 0
    uv_missing = []
    for obj in meshes:
        obj.data.calc_loop_triangles()
        triangles += len(obj.data.loop_triangles)
        if not obj.data.uv_layers:
            uv_missing.append(obj.name)

    materials = sorted({slot.material.name for obj in meshes for slot in obj.material_slots if slot.material})
    images = sorted({node.image.name for mat in bpy.data.materials if mat.use_nodes
                     for node in mat.node_tree.nodes if node.type == "TEX_IMAGE" and node.image})

    node_names = sorted(obj.name for obj in bpy.context.scene.objects)
    required = REQUIRED_NODES.get(args.id, [])
    missing_nodes = [name for name in required if name not in node_names]

    failures = []
    warnings = []

    if triangles > args.max_tris:
        failures.append(f"triangles {triangles} > budget {args.max_tris}")
    if missing_nodes:
        failures.append(f"missing contract nodes: {', '.join(missing_nodes)}")
    if uv_missing:
        failures.append(f"meshes without UVs: {', '.join(uv_missing[:8])}")

    span_error = abs(size.x - args.span) / args.span
    length_error = abs(size.y - args.length) / args.length
    if span_error > args.tolerance:
        warnings.append(f"span {size.x:.2f} m differs from {args.span:.2f} m by {span_error * 100:.1f}%")
    if length_error > args.tolerance:
        warnings.append(f"length {size.y:.2f} m differs from {args.length:.2f} m by {length_error * 100:.1f}%")

    # Origin sanity: the CoG must lie inside the bounding box.
    if not (low.x <= 0.0 <= high.x and low.y <= 0.0 <= high.y and low.z <= 0.0 <= high.z):
        failures.append("origin (CoG) is outside the model bounds")

    meta = {
        "id": args.id,
        "source": os.path.basename(glb),
        "bounds": {
            "min": [round(v, 4) for v in low],
            "max": [round(v, 4) for v in high],
            "size": [round(v, 4) for v in size],
        },
        "expected_span_m": args.span,
        "expected_length_m": args.length,
        "triangles": triangles,
        "objects": len(bpy.context.scene.objects),
        "meshes": len(meshes),
        "materials": materials,
        "images": images,
        "nodes": node_names,
        "missing_contract_nodes": missing_nodes,
        "warnings": warnings,
        "failures": failures,
    }

    out = args.out or (os.path.splitext(glb)[0] + ".gltf_meta.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(meta, handle, indent=2)
        handle.write("\n")

    print(json.dumps(meta, indent=2))
    print(f"Wrote {out}")
    if failures:
        print("FAIL: " + "; ".join(failures))
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(parse_args()))
