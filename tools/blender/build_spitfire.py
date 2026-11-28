"""Build a first-pass Spitfire LF Mk IX blockout.

Run:

    blender --background --python tools/blender/build_spitfire.py

Outputs:

    game/assets/aircraft/spitfire-mk9/model/spitfire-mk9_lod0.glb
    game/assets/aircraft/spitfire-mk9/model/spitfire-mk9.blend
    docs/media/aircraft/spitfire-mk9-blockout-*.png   (via render_turntable.py)

The model is intentionally a *measured blockout*: correct proportions, control
surface segmentation and node names, with solid materials. The texture/panel-line
pass builds on this scene.
"""

from __future__ import annotations

import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from aircraft_common import (  # noqa: E402
    airfoil_loop,
    attach_occlusion,
    bake_ao,
    bake_detail_normal,
    box,
    collection_objects,
    cylinder,
    cylinder_between,
    descendants,
    export_glb,
    extrude_outline,
    image_material,
    join_meshes,
    link_object,
    loft,
    material,
    mesh_object,
    naca_offset,
    new_collection,
    reset_scene,
    rivet_strip,
    scale_uvs,
    superellipse,
    tube_from_points,
    uv_unwrap_all,
)

# ---------------------------------------------------------------------------
# Dimensions (metres, Blender frame: +Y nose, +Z up, origin at CoG)
# ---------------------------------------------------------------------------

SPAN = 11.23
SPAN_HALF = SPAN / 2.0
LENGTH = 9.47

WING_ROOT_CHORD = 2.55
WING_TIP_CHORD = 0.28
WING_ROOT_LE = 0.95
WING_TIP_LE = 0.60
WING_ROOT_Z = -0.28
WING_DIHEDRAL = math.radians(6.0)
WING_ROOT_THICKNESS = 0.13
WING_TIP_THICKNESS = 0.09
WING_ROOT_CAMBER = 0.02

TAIL_ROOT_LE = -3.55
TAIL_ROOT_CHORD = 1.25
TAIL_TIP_CHORD = 0.08
TAIL_TIP_LE = -3.90
TAIL_Z = 0.16
TAIL_SPAN_HALF = 1.65

PROP_Y = 4.00
SPINNER_TIP_Y = 4.37
PROP_RADIUS = 1.64

FUSELAGE_STATIONS = [
    # (y, half width, z top, z bottom)
    (3.85, 0.29, 0.34, -0.34),
    (3.55, 0.36, 0.40, -0.42),
    (3.10, 0.43, 0.48, -0.50),
    (2.55, 0.46, 0.54, -0.54),
    (2.00, 0.48, 0.58, -0.56),
    (1.50, 0.49, 0.62, -0.55),
    (1.10, 0.49, 0.63, -0.54),
    (0.60, 0.50, 0.62, -0.53),
    (0.00, 0.50, 0.60, -0.52),
    (-0.60, 0.49, 0.57, -0.50),
    (-1.20, 0.47, 0.53, -0.47),
    (-1.80, 0.44, 0.48, -0.42),
    (-2.40, 0.39, 0.43, -0.37),
    (-3.00, 0.33, 0.38, -0.32),
    (-3.60, 0.26, 0.33, -0.26),
    (-4.20, 0.19, 0.29, -0.19),
    (-4.70, 0.13, 0.26, -0.13),
    (-5.10, 0.045, 0.21, -0.045),
]

FUSELAGE_RADIAL_SEGMENTS = 32


# ---------------------------------------------------------------------------
# Geometry helpers
# ---------------------------------------------------------------------------


def section_ring(station, segments: int = FUSELAGE_RADIAL_SEGMENTS):
    y, width, z_top, z_bottom = station
    return superellipse(y, width, z_top, z_bottom, count=segments, exponent=2.35)


def build_fuselage(exterior, mat):
    """Split the fuselage so cockpit view can hide only the centre skin."""
    stations = FUSELAGE_STATIONS

    def rings_between(y_start: float, y_end: float):
        selected = [s for s in stations if y_end <= s[0] <= y_start]
        # Keep the exact split station at both ends.
        for endpoint in (y_start, y_end):
            if not any(abs(s[0] - endpoint) < 1e-6 for s in selected):
                raise ValueError(f"split station {endpoint} not in FUSELAGE_STATIONS")
        return [section_ring(s) for s in selected]

    front = loft("FuselageFront", rings_between(3.85, 1.50), exterior, mat,
                 cap_start=True, cap_end=False)
    centre = loft("FuselageCenter", rings_between(1.50, -1.20), exterior, mat,
                  cap_start=False, cap_end=False)
    rear = loft("FuselageRear", rings_between(-1.20, -5.10), exterior, mat,
                cap_start=False, cap_end=True)
    return front, centre, rear


def wing_planform(t: float) -> tuple[float, float, float, float]:
    """Return (x, chord, leading-edge y, centre z) for span fraction t in [-1, 1]."""
    a = abs(t)
    chord = WING_TIP_CHORD + (WING_ROOT_CHORD - WING_TIP_CHORD) * (1.0 - a * a) ** 0.65
    le_y = WING_ROOT_LE - (WING_ROOT_LE - WING_TIP_LE) * (a**1.7)
    z = WING_ROOT_Z + math.tan(WING_DIHEDRAL) * SPAN_HALF * a
    return SPAN_HALF * t, chord, le_y, z


def wing_point(t: float, chord_fraction: float, upper: bool) -> tuple[float, float, float]:
    x, chord, le_y, z_center = wing_planform(t)
    thickness = WING_ROOT_THICKNESS + (WING_TIP_THICKNESS - WING_ROOT_THICKNESS) * abs(t)
    camber = WING_ROOT_CAMBER * (1.0 - 0.6 * abs(t))
    yc, yt, theta = naca_offset(camber, 0.4, thickness, chord_fraction)
    offset = yc + yt if upper else yc - yt
    # Airfoil chord runs backwards in Y; thickness is vertical in Z.
    dy = -chord_fraction * chord
    dz = offset * chord
    twist = math.radians(-2.0) * (abs(t) ** 1.5)
    return (
        x,
        le_y + dy * math.cos(twist) - dz * math.sin(twist),
        z_center + dy * math.sin(twist) + dz * math.cos(twist),
    )


def build_wing(exterior, mat):
    span_stations = [
        math.sin(0.5 * math.pi * (-1.0 + 2.0 * i / 28.0)) for i in range(29)
    ]
    rings = []
    for t in span_stations:
        section = []
        for cx, cz in airfoil_loop(WING_ROOT_CAMBER, 0.4, 0.13, count=26):
            x, chord, le_y, z_center = wing_planform(t)
            thickness = WING_ROOT_THICKNESS + (WING_TIP_THICKNESS - WING_ROOT_THICKNESS) * abs(t)
            # Use the section directly; camber/thickness vary across the span.
            scale = thickness / 0.13
            dy = -cx * chord
            dz = cz * chord * scale
            twist = math.radians(-2.0) * (abs(t) ** 1.5)
            section.append((
                x,
                le_y + dy * math.cos(twist) - dz * math.sin(twist),
                z_center + dy * math.sin(twist) + dz * math.cos(twist),
            ))
        rings.append(section)
    loft("Wing", rings, exterior, mat, cap_start=True, cap_end=True,
         split_angle_deg=32.0)


def build_wing_panel(name, t0, t1, exterior, mat, chord0=0.735, chord1=1.0):
    """Build a trailing-edge control surface with its origin on the hinge line."""
    t_mid = 0.5 * (t0 + t1)
    x_mid, chord_mid, le_mid, z_mid = wing_planform(t_mid)
    hinge_y = le_mid - chord0 * chord_mid
    hinge = (x_mid, hinge_y, z_mid)

    span_stations = [t0 + (t1 - t0) * i / 8.0 for i in range(9)]
    rings = []
    for t in span_stations:
        upper0 = wing_point(t, chord0, upper=True)
        upper1 = wing_point(t, chord1, upper=True)
        lower1 = wing_point(t, chord1, upper=False)
        lower0 = wing_point(t, chord0, upper=False)
        # Small outward offset hides the underlying wing surface at the hinge.
        eps = 0.006
        ring = [
            (upper0[0] - hinge[0], upper0[1] - hinge[1], upper0[2] - hinge[2] + eps),
            (upper1[0] - hinge[0], upper1[1] - hinge[1], upper1[2] - hinge[2] + eps),
            (lower1[0] - hinge[0], lower1[1] - hinge[1], lower1[2] - hinge[2] - eps),
            (lower0[0] - hinge[0], lower0[1] - hinge[1], lower0[2] - hinge[2] - eps),
        ]
        rings.append(ring)

    obj = loft(name, rings, exterior, mat, cap_start=True, cap_end=True,
               split_angle_deg=32.0)
    obj.location = hinge
    return obj


def build_tail(exterior, mat):
    # Tailplane: symmetric section, span along X.
    span_stations = [math.sin(0.5 * math.pi * (-1.0 + 2.0 * i / 16.0)) for i in range(17)]
    rings = []
    for t in span_stations:
        a = abs(t)
        x = TAIL_SPAN_HALF * t
        chord = TAIL_TIP_CHORD + (TAIL_ROOT_CHORD - TAIL_TIP_CHORD) * (1.0 - a * a) ** 0.7
        le_y = TAIL_ROOT_LE - (TAIL_ROOT_LE - TAIL_TIP_LE) * a
        section = []
        for cx, cz in airfoil_loop(0.0, 0.4, 0.085, count=18):
            section.append((x, le_y - cx * chord, TAIL_Z + cz * chord))
        rings.append(section)
    loft("Tailplane", rings, exterior, mat, cap_start=True, cap_end=True,
         split_angle_deg=32.0)

    # Elevators: trailing 30% of the tailplane.
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        t0, t1 = side * 0.08, side * 0.95
        t_mid = 0.5 * (t0 + t1)
        a = abs(t_mid)
        chord_mid = TAIL_TIP_CHORD + (TAIL_ROOT_CHORD - TAIL_TIP_CHORD) * (1.0 - a * a) ** 0.7
        le_mid = TAIL_ROOT_LE - (TAIL_ROOT_LE - TAIL_TIP_LE) * a
        hinge = (TAIL_SPAN_HALF * t_mid, le_mid - 0.70 * chord_mid, TAIL_Z)
        rings = []
        for i in range(7):
            t = t0 + (t1 - t0) * i / 6.0
            aa = abs(t)
            chord = TAIL_TIP_CHORD + (TAIL_ROOT_CHORD - TAIL_TIP_CHORD) * (1.0 - aa * aa) ** 0.7
            le_y = TAIL_ROOT_LE - (TAIL_ROOT_LE - TAIL_TIP_LE) * aa
            x = TAIL_SPAN_HALF * t
            section = airfoil_loop(0.0, 0.4, 0.085, count=18)
            # Take the upper and lower surfaces at 70% and 100% chord.
            upper0 = (x, le_y - 0.70 * chord, TAIL_Z + 0.10 * chord * 0.05)
            upper1 = (x, le_y - chord, TAIL_Z)
            lower1 = (x, le_y - chord, TAIL_Z)
            lower0 = (x, le_y - 0.70 * chord, TAIL_Z - 0.10 * chord * 0.05)
            rings.append([
                (upper0[0] - hinge[0], upper0[1] - hinge[1], upper0[2] - hinge[2]),
                (upper1[0] - hinge[0], upper1[1] - hinge[1], upper1[2] - hinge[2]),
                (lower1[0] - hinge[0], lower1[1] - hinge[1], lower1[2] - hinge[2]),
                (lower0[0] - hinge[0], lower0[1] - hinge[1], lower0[2] - hinge[2]),
            ])
        obj = loft(f"Elevator{suffix}", rings, exterior, mat, cap_start=True, cap_end=True,
                   split_angle_deg=32.0)
        obj.location = hinge


def build_fin(exterior, mat):
    # Fin as a lofted symmetric airfoil: chord along Y, thickness along X.
    fin_stations = [
        # (z, leading-edge y, chord, thickness ratio)
        (0.08, -3.56, 1.39, 0.10),
        (0.30, -3.60, 1.34, 0.10),
        (0.55, -3.68, 1.27, 0.10),
        (0.80, -3.80, 1.17, 0.10),
        (1.05, -3.96, 1.00, 0.095),
        (1.25, -4.12, 0.82, 0.09),
        (1.40, -4.26, 0.64, 0.085),
        (1.50, -4.38, 0.48, 0.08),
        (1.56, -4.50, 0.35, 0.07),
        (1.59, -4.62, 0.23, 0.06),
    ]
    rings = []
    for z, le_y, chord, thickness in fin_stations:
        ring = []
        for cx, cz in airfoil_loop(0.0, 0.4, thickness, count=20):
            ring.append((cz * chord, le_y - cx * chord, z))
        rings.append(ring)
    loft("Fin", rings, exterior, mat, cap_start=True, cap_end=True,
         split_angle_deg=32.0)

    rudder_outline = [
        (-4.88, 0.12), (-4.90, 0.45), (-4.92, 0.78), (-4.93, 1.05),
        (-4.94, 1.28), (-5.02, 1.38), (-5.12, 1.30), (-5.16, 1.05),
        (-5.18, 0.72), (-5.17, 0.40), (-5.15, 0.12),
    ]
    hinge_z = 0.62
    hinge_y = -4.88
    verts = []
    half = 0.04
    for y, z in rudder_outline:
        verts.append((-half, y - hinge_y, z - hinge_z))
    for y, z in rudder_outline:
        verts.append((half, y - hinge_y, z - hinge_z))
    n = len(rudder_outline)
    faces = []
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, n + j, n + i))
    faces.append(tuple(range(n - 1, -1, -1)))
    faces.append(tuple(range(n, 2 * n)))
    rudder = mesh_object("Rudder", verts, faces, exterior, mat, smooth=False)
    rudder.location = (0.0, hinge_y, hinge_z)


def empty(name: str, location, collection):
    obj = bpy.data.objects.new(name, None)
    collection.objects.link(obj)
    obj.empty_display_type = "PLAIN_AXES"
    obj.empty_display_size = 0.18
    obj.location = location
    return obj


def parent_keep(child, parent):
    bpy.context.view_layer.update()
    child.parent = parent
    child.matrix_parent_inverse = parent.matrix_world.inverted()


def build_gear(exterior, metal, tyre):
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        pivot = empty(f"GearMain{suffix}", (side * 0.85, 0.85, -0.45), exterior)
        leg = cylinder_between(
            f"GearLeg{suffix}",
            (side * 0.85, 0.85, -0.45),
            (side * 0.93, 0.85, -1.17),
            0.055,
            exterior,
            metal,
        )
        wheel = cylinder(
            f"GearWheel{suffix}",
            (side * 0.95, 0.85, -1.17),
            0.33,
            0.20,
            exterior,
            tyre,
            axis=(1.0, 0.0, 0.0),
            vertices=24,
        )
        hub = cylinder(
            f"GearHub{suffix}",
            (side * 0.95, 0.85, -1.17),
            0.11,
            0.21,
            exterior,
            metal,
            axis=(1.0, 0.0, 0.0),
            vertices=16,
        )
        door = box(
            f"GearDoor{suffix}",
            (side * 1.00, 0.85, -0.78),
            (0.035, 0.52, 0.52),
            exterior,
            metal,
        )
        for part in (leg, wheel, hub, door):
            parent_keep(part, pivot)

    tail_pivot = empty("GearTail", (0.0, -4.78, -0.18), exterior)
    tail_leg = cylinder_between(
        "GearTailLeg",
        (0.0, -4.78, -0.18),
        (0.0, -4.90, -0.26),
        0.045,
        exterior,
        metal,
    )
    tail_wheel = cylinder(
        "GearTailWheel",
        (0.0, -4.90, -0.26),
        0.15,
        0.10,
        exterior,
        tyre,
        axis=(1.0, 0.0, 0.0),
        vertices=18,
    )
    for part in (tail_leg, tail_wheel):
        parent_keep(part, tail_pivot)


def build_propeller(exterior, metal, dark):
    # Build the spinner as a loft of circular rings.
    rings = []
    for y, radius in ((3.84, 0.30), (4.00, 0.285), (4.14, 0.23), (4.28, 0.13), (4.37, 0.015)):
        rings.append([
            (radius * math.cos(2.0 * math.pi * i / 32.0), y,
             radius * math.sin(2.0 * math.pi * i / 32.0))
            for i in range(32)
        ])
    loft("Spinner", rings, exterior, metal, cap_start=True, cap_end=True,
         split_angle_deg=45.0)

    prop = empty("Propeller", (0.0, PROP_Y, 0.0), exterior)

    # One blade shape, repeated four times around the Y axis.
    all_verts = []
    all_faces = []
    for blade in range(4):
        angle = 0.5 * math.pi * blade
        ca, sa = math.cos(angle), math.sin(angle)
        base = len(all_verts)
        stations = [0.28, 0.50, 0.80, 1.10, 1.35, 1.55, 1.66]
        ring_indices = []
        for r in stations:
            chord = 0.22 + 0.16 * math.sin(math.pi * min(1.0, (r - 0.28) / 1.38))
            pitch = math.radians(48.0 - 28.0 * (r - 0.28) / 1.38)
            section = airfoil_loop(0.04, 0.4, 0.12, count=14)
            start = len(all_verts)
            for cx, cz in section:
                z_local = (cx - 0.35) * chord
                y_local = cz * chord
                y_rot = y_local * math.cos(pitch) - z_local * math.sin(pitch)
                z_rot = y_local * math.sin(pitch) + z_local * math.cos(pitch)
                # Blade extends radially along +X then rotates about Y.
                x = r * ca - z_rot * sa
                z = r * sa + z_rot * ca
                all_verts.append((x, y_rot, z))
            ring_indices.append((start, len(section)))
        # Faces between rings.
        for i in range(len(ring_indices) - 1):
            s0, n = ring_indices[i]
            s1, _ = ring_indices[i + 1]
            for j in range(n):
                j2 = (j + 1) % n
                all_faces.append((s0 + j, s0 + j2, s1 + j2, s1 + j))
        s0, n = ring_indices[0]
        all_faces.append(tuple(range(s0 + n - 1, s0 - 1, -1)))
        s1, n = ring_indices[-1]
        all_faces.append(tuple(range(s1, s1 + n)))

    blades = mesh_object("PropellerBlades", all_verts, all_faces, exterior, dark,
                         smooth=True, split_angle_deg=35.0)
    blades.location = (0.0, PROP_Y, 0.0)
    parent_keep(blades, prop)

    # Blade root fairing.
    root = cylinder("PropellerRoot", (0.0, PROP_Y, 0.0), 0.16, 0.22, exterior, dark,
                    axis=(0.0, 1.0, 0.0), vertices=20)
    parent_keep(root, prop)


def canopy_ring(y, width, top, base=0.60, count=21, exponent=2.4):
    """Half-superellipse canopy section: flatter top, more vertical sides."""
    power = 2.0 / exponent
    ring = []
    for i in range(count):
        theta = math.pi * i / (count - 1)
        c = math.cos(theta)
        s = math.sin(theta)
        x = width * math.copysign(abs(c) ** power, c)
        z = base + (top - base) * (abs(s) ** power)
        ring.append((x, y, z))
    return ring


def build_canopy(exterior, glass, frame, airframe):
    stations = [
        # (y, half width, top z)
        (1.31, 0.24, 0.66), (1.24, 0.31, 0.86), (1.16, 0.36, 0.95),
        (1.00, 0.39, 1.00), (0.70, 0.40, 1.02), (0.30, 0.40, 1.03),
        (-0.10, 0.39, 1.02), (-0.45, 0.37, 0.99), (-0.75, 0.33, 0.93),
        (-1.00, 0.28, 0.85), (-1.18, 0.21, 0.75),
    ]
    count = 21
    rings = [canopy_ring(y, width, top, count=count) for y, width, top in stations]
    mesh_object(
        "Canopy",
        [v for ring in rings for v in ring],
        [
            (i * count + j, i * count + j + 1,
             (i + 1) * count + j + 1, (i + 1) * count + j)
            for i in range(len(rings) - 1)
            for j in range(count - 1)
        ],
        exterior,
        glass,
        smooth=True,
        split_angle_deg=60.0,
    )

    # Arch frames: windscreen, hood front, mid, rear.
    for index, radius in ((0, 0.020), (2, 0.023), (4, 0.018), (6, 0.018),
                          (8, 0.018), (10, 0.022)):
        y, width, top = stations[index]
        points = [
            (width * math.cos(math.pi * i / 14.0), y,
             0.60 + (top - 0.60) * math.sin(math.pi * i / 14.0))
            for i in range(15)
        ]
        tube_from_points(f"CanopyFrame{index}", points, radius, exterior, frame)

    # Longitudinal sill rails and the top spine.
    for side in (-1.0, 1.0):
        points = [(side * width, y, 0.60) for y, width, _ in stations]
        tube_from_points(f"CanopyRail{'L' if side < 0 else 'R'}", points, 0.016,
                         exterior, frame)
    spine_points = [(0.0, y, top) for y, _, top in stations]
    tube_from_points("CanopySpine", spine_points, 0.015, exterior, frame)

    # Headrest fairing behind the hood.
    fairing_stations = [
        (-1.10, 0.23, 0.72, 0.38),
        (-1.28, 0.21, 0.71, 0.40),
        (-1.48, 0.17, 0.68, 0.42),
        (-1.66, 0.11, 0.64, 0.44),
        (-1.78, 0.05, 0.60, 0.46),
    ]
    fairing_rings = [
        superellipse(y, width, top, bottom, count=18, exponent=2.2)
        for y, width, top, bottom in fairing_stations
    ]
    loft("HeadrestFairing", fairing_rings, exterior, airframe,
         cap_start=True, cap_end=True, split_angle_deg=40.0)


def build_cockpit(cockpit, mats):
    interior = mats["interior"]
    dark = mats["dark"]
    metal = mats["metal"]
    leather = mats["leather"]
    face = mats["gauge_face"]
    harness = mats["harness"]

    box("CockpitFloor", (0.0, -0.05, -0.10), (0.86, 1.75, 0.05), cockpit, interior)
    box("CockpitWallL", (-0.44, -0.05, 0.24), (0.05, 1.75, 0.62), cockpit, interior)
    box("CockpitWallR", (0.44, -0.05, 0.24), (0.05, 1.75, 0.62), cockpit, interior)
    box("CockpitRear", (0.0, -0.92, 0.24), (0.90, 0.06, 0.64), cockpit, interior)
    box("CockpitFront", (0.0, 0.86, 0.24), (0.90, 0.06, 0.64), cockpit, interior)

    # Coaming and sill around the opening. The rails sit just inboard of the
    # canopy so an eye at z≈0.72 sees the panel over the coaming.
    box("CoamingFront", (0.0, 0.86, 0.60), (0.98, 0.12, 0.08), cockpit, leather,
        rotation=(math.radians(-8.0), 0.0, 0.0))
    box("CoamingL", (-0.48, -0.05, 0.60), (0.08, 1.85, 0.07), cockpit, leather)
    box("CoamingR", (0.48, -0.05, 0.60), (0.08, 1.85, 0.07), cockpit, leather)
    box("CoamingRear", (0.0, -0.98, 0.60), (0.98, 0.10, 0.08), cockpit, leather)
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        box(f"CanopySill{suffix}", (side * 0.43, -0.05, 0.575),
            (0.035, 1.60, 0.025), cockpit, metal)
        for index, y in enumerate((-0.72, -0.36, 0.00, 0.36, 0.68)):
            box(f"SidewallRib{suffix}{index}", (side * 0.455, y, 0.28),
                (0.025, 0.045, 0.46), cockpit, interior)

    # Three-section instrument panel with a glare shield; the centre section
    # carries two rows of four gauges, the angled side sections carry switches.
    panel_tilt = math.radians(-10.0)
    box("InstrumentPanel", (0.0, 0.775, 0.42), (0.50, 0.07, 0.42), cockpit, dark,
        rotation=(panel_tilt, 0.0, 0.0))
    box("PanelFace", (0.0, 0.740, 0.43), (0.46, 0.02, 0.36), cockpit, face,
        rotation=(panel_tilt, 0.0, 0.0))
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        box(f"PanelSide{suffix}", (side * 0.36, 0.79, 0.43),
            (0.24, 0.06, 0.34), cockpit, dark,
            rotation=(panel_tilt, 0.0, side * math.radians(24.0)))
        box(f"SwitchBlock{suffix}", (side * 0.36, 0.755, 0.39),
            (0.16, 0.02, 0.16), cockpit, face,
            rotation=(panel_tilt, 0.0, side * math.radians(24.0)))
        for index in range(3):
            cylinder(f"Switch{suffix}{index}",
                     (side * (0.31 + 0.05 * index), 0.745, 0.41),
                     0.008, 0.035, cockpit, metal, axis=(0.0, 1.0, 0.0),
                     vertices=10)
    box("GlareShield", (0.0, 0.785, 0.645), (0.98, 0.16, 0.035), cockpit, leather,
        rotation=(math.radians(-14.0), 0.0, 0.0))

    for i in range(8):
        x = (-1.5 + (i % 4)) * 0.115
        z = 0.505 - (i // 4) * 0.135
        cylinder(f"Gauge{i}", (x, 0.730, z), 0.052, 0.020, cockpit, metal,
                 axis=(0.0, 1.0, 0.0), vertices=20)
        cylinder(f"GaugeFace{i}", (x, 0.722, z), 0.041, 0.018, cockpit, face,
                 axis=(0.0, 1.0, 0.0), vertices=20)
    cylinder("Compass", (0.0, 0.725, 0.62), 0.030, 0.022, cockpit, dark,
             axis=(0.0, 1.0, 0.0), vertices=16)

    # Seat, cushions, armour, headrest pad and harness.
    box("SeatPan", (0.0, -0.32, 0.02), (0.44, 0.42, 0.08), cockpit, leather)
    box("SeatCushion", (0.0, -0.30, 0.075), (0.38, 0.34, 0.045), cockpit, leather)
    box("SeatBack", (0.0, -0.56, 0.30), (0.44, 0.09, 0.52), cockpit, leather)
    box("BackCushion", (0.0, -0.512, 0.32), (0.36, 0.045, 0.40), cockpit, leather)
    box("SeatArmour", (0.0, -0.63, 0.32), (0.48, 0.04, 0.58), cockpit, dark)
    box("Headrest", (0.0, -0.575, 0.625), (0.19, 0.10, 0.13), cockpit, leather)
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        box(f"Harness{suffix}", (side * 0.11, -0.49, 0.36),
            (0.050, 0.030, 0.44), cockpit, harness,
            rotation=(math.radians(7.0), 0.0, 0.0))
        box(f"HarnessBuckle{suffix}", (side * 0.11, -0.455, 0.175),
            (0.075, 0.045, 0.055), cockpit, metal)
    box("LapBelt", (0.0, -0.40, 0.105), (0.36, 0.05, 0.045), cockpit, harness)
    box("HarnessAnchor", (0.0, -0.66, 0.58), (0.30, 0.03, 0.035), cockpit, metal)

    # Control column, grip, boot and pedals.
    cylinder_between("ControlColumn", (0.0, 0.42, -0.06), (0.0, 0.37, 0.25),
                     0.026, cockpit, metal)
    box("ControlBoot", (0.0, 0.42, -0.02), (0.11, 0.14, 0.08), cockpit, dark)
    box("ControlGrip", (0.0, 0.365, 0.295), (0.085, 0.11, 0.11), cockpit, dark)
    cylinder("ControlButton", (0.0, 0.345, 0.35), 0.012, 0.02, cockpit, metal,
             axis=(0.0, 0.0, 1.0), vertices=10)
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        box(f"Pedal{suffix}", (side * 0.17, 0.755, -0.035),
            (0.15, 0.055, 0.19), cockpit, metal,
            rotation=(math.radians(18.0), 0.0, 0.0))

    # Throttle quadrant on the left wall, friction lever and grip.
    box("ThrottleBase", (-0.355, 0.48, 0.18), (0.10, 0.34, 0.07), cockpit, dark)
    cylinder_between("ThrottleLever", (-0.355, 0.50, 0.20),
                     (-0.355, 0.42, 0.34), 0.018, cockpit, metal)
    box("ThrottleGrip", (-0.355, 0.405, 0.365), (0.045, 0.075, 0.045),
        cockpit, dark)
    box("MixtureBase", (-0.36, 0.30, 0.17), (0.07, 0.16, 0.05), cockpit, dark)
    box("MixtureGrip", (-0.36, 0.26, 0.23), (0.045, 0.06, 0.035), cockpit, dark)

    # Reflector gunsight with a sun shade and mounting arm.
    box("GunsightBase", (0.0, 0.72, 0.57), (0.13, 0.14, 0.08), cockpit, dark)
    box("GunsightArm", (0.0, 0.70, 0.61), (0.05, 0.06, 0.08), cockpit, metal)
    box("GunsightGlass", (0.0, 0.68, 0.68), (0.16, 0.012, 0.13), cockpit,
        mats["glass"])
    box("GunsightShade", (0.0, 0.665, 0.72), (0.18, 0.09, 0.025), cockpit, dark)

    # Map pocket on the right wall.
    box("MapPocket", (0.405, -0.42, 0.22), (0.02, 0.26, 0.20), cockpit, interior)


def fuselage_station_at(y: float):
    """Interpolate (half width, z top, z bottom) at an arbitrary station."""
    stations = FUSELAGE_STATIONS
    if y >= stations[0][0]:
        return stations[0][1], stations[0][2], stations[0][3]
    if y <= stations[-1][0]:
        return stations[-1][1], stations[-1][2], stations[-1][3]
    for i in range(len(stations) - 1):
        y0, w0, t0, b0 = stations[i]
        y1, w1, t1, b1 = stations[i + 1]
        if y1 <= y <= y0:
            f = (y0 - y) / (y0 - y1)
            return (
                w0 + (w1 - w0) * f,
                t0 + (t1 - t0) * f,
                b0 + (b1 - b0) * f,
            )
    raise ValueError(y)


def fuselage_surface_point(y: float, angle: float, offset: float = 1.002):
    width, z_top, z_bottom = fuselage_station_at(y)
    z_center = 0.5 * (z_top + z_bottom)
    z_radius = 0.5 * (z_top - z_bottom)
    power = 2.0 / 2.35
    c = math.cos(angle)
    s = math.sin(angle)
    x = width * offset * math.copysign(abs(c) ** power, c)
    z = z_center + z_radius * offset * math.copysign(abs(s) ** power, s)
    return x, y, z


def fuselage_side_x(y: float, z: float, side: float, offset: float = 0.008):
    width, z_top, z_bottom = fuselage_station_at(y)
    z_center = 0.5 * (z_top + z_bottom)
    z_radius = max(1e-6, 0.5 * (z_top - z_bottom))
    t = min(1.0, abs((z - z_center) / z_radius))
    x = width * (max(0.0, 1.0 - t**2.35) ** (1.0 / 2.35))
    return side * (x + offset)


def disc_mesh(name, center, ring_points, collection, mat):
    verts = [center] + list(ring_points)
    n = len(ring_points)
    faces = [(0, i + 1, (i + 1) % n + 1) for i in range(n)]
    return mesh_object(name, verts, faces, collection, mat, smooth=False)


def build_roundel_fuselage(name, center_y, center_z, radius, side, exterior, mats):
    segments = 28
    for suffix, r, mat_name, offset in (
        ("blue", radius, "roundel_blue", 0.006),
        ("white", radius * 0.67, "roundel_white", 0.010),
        ("red", radius * 0.31, "roundel_red", 0.014),
    ):
        center = (fuselage_side_x(center_y, center_z, side, offset), center_y, center_z)
        ring = []
        for i in range(segments):
            a = 2.0 * math.pi * i / segments
            y = center_y + r * math.cos(a)
            z = center_z + r * math.sin(a)
            ring.append((fuselage_side_x(y, z, side, offset), y, z))
        disc_mesh(f"{name}_{suffix}", center, ring, exterior, mats[mat_name])


def build_roundel_wing(name, center_x, center_y, radius, upper, exterior, mats):
    segments = 28
    span_half = SPAN_HALF
    sign = 1.0 if center_x >= 0 else -1.0

    def surface_z(x, y):
        t = max(-1.0, min(1.0, x / span_half))
        _, chord, le_y, _ = wing_planform(t)
        c = max(0.0, min(1.0, (le_y - y) / max(chord, 1e-6)))
        return wing_point(t, c, upper)

    for suffix, r, mat_name, offset in (
        ("blue", radius, "roundel_blue", 0.010),
        ("white", radius * 0.67, "roundel_white", 0.014),
        ("red", radius * 0.31, "roundel_red", 0.018),
    ):
        cx = center_x + sign * 0.0
        p = surface_z(cx, center_y)
        center = (cx, center_y, p[2] + (-offset if not upper else offset))
        ring = []
        for i in range(segments):
            a = 2.0 * math.pi * i / segments
            x = center_x + r * math.cos(a)
            y = center_y + r * math.sin(a)
            q = surface_z(x, y)
            ring.append((x, y, q[2] + (-offset if not upper else offset)))
        disc_mesh(f"{name}_{suffix}", center, ring, exterior, mats[mat_name])


def build_panel_lines(exterior, mats):
    line = mats["panel_line"]
    # Fuselage frame stations.
    for y in (3.60, 3.05, 2.50, 1.95, 1.42, 0.90, 0.30, -0.30, -0.90,
              -1.50, -2.10, -2.70, -3.30, -3.90, -4.45):
        points = [fuselage_surface_point(y, 2.0 * math.pi * i / 28.0, 1.004)
                  for i in range(28)]
        tube_from_points(f"PanelFrame{y:.1f}", points, 0.012, exterior, line, resolution=0)
        rivet_strip(f"RivetsFrame{y:.1f}", points, exterior, line)

    # Fuselage longitudinal seams.
    y_values = [3.70 - 0.18 * i for i in range(47)]
    for angle in (0.0, math.pi * 0.5, math.pi, math.pi * 1.5,
                  math.pi * 0.25, math.pi * 0.75, math.pi * 1.25, math.pi * 1.75):
        points = [fuselage_surface_point(y, angle, 1.004)
                  for y in y_values if y <= 3.85 and y >= -4.9]
        tube_from_points(f"PanelLongitude{angle:.2f}", points, 0.010, exterior, line, resolution=0)
        rivet_strip(f"RivetsLongitude{angle:.2f}", points, exterior, line)

    # Wing ribs (chordwise) and spanwise joints, both surfaces.
    for t in (-0.90, -0.70, -0.50, -0.30, -0.10, 0.10, 0.30, 0.50, 0.70, 0.90):
        for upper in (True, False):
            points = [wing_point(t, c / 10.0, upper) for c in range(1, 10)]
            tube_from_points(f"WingRib{t:+.2f}{upper}", points, 0.010, exterior, line,
                             resolution=0)
            rivet_strip(f"RivetsWingRib{t:+.2f}{upper}", points, exterior, line)
    for c in (0.20, 0.50, 0.80):
        for upper in (True, False):
            points = [wing_point(-0.96 + 1.92 * i / 14.0, c, upper) for i in range(15)]
            tube_from_points(f"WingSpan{c:.2f}{upper}", points, 0.010, exterior, line,
                             resolution=0)
            rivet_strip(f"RivetsWingSpan{c:.2f}{upper}", points, exterior, line)

    # Tailplane ribs.
    for t in (-0.80, -0.40, 0.40, 0.80):
        for upper in (True, False):
            points = []
            for i in range(1, 7):
                c = i / 7.0
                a = abs(t)
                x = TAIL_SPAN_HALF * t
                chord = TAIL_TIP_CHORD + (TAIL_ROOT_CHORD - TAIL_TIP_CHORD) * (1.0 - a * a) ** 0.7
                le_y = TAIL_ROOT_LE - (TAIL_ROOT_LE - TAIL_TIP_LE) * a
                dz = 0.085 * chord * 0.5 if upper else -0.085 * chord * 0.5
                points.append((x, le_y - c * chord, TAIL_Z + dz))
            tube_from_points(f"TailRib{t:+.2f}{upper}", points, 0.009, exterior, line,
                             resolution=0)
            rivet_strip(f"RivetsTailRib{t:+.2f}{upper}", points, exterior, line)

    # National markings: fuselage sides, upper and lower wings.
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        build_roundel_fuselage(f"RoundelFuselage{suffix}", -0.95, 0.10, 0.42,
                               side, exterior, mats)
        build_roundel_wing(f"RoundelWingTop{suffix}", side * 3.40, 0.28, 0.52,
                           True, exterior, mats)
        build_roundel_wing(f"RoundelWingBottom{suffix}", side * 3.40, 0.28, 0.52,
                           False, exterior, mats)


def build_details(exterior, mats):
    metal = mats["metal"]
    dark = mats["dark"]

    # Exhaust stacks: six per side.
    for side in (-1.0, 1.0):
        for i in range(6):
            y = 3.08 - i * 0.15
            box(f"Exhaust{side:+.0f}_{i}",
                (side * 0.435, y, 0.14), (0.075, 0.13, 0.085),
                exterior, dark, rotation=(0.0, 0.0, math.radians(side * 12.0)))

    # Radiator and oil cooler under the wings.
    for side in (-1.0, 1.0):
        box(f"Radiator{side:+.0f}", (side * 1.30, -0.10, -0.54),
            (0.72, 1.00, 0.22), exterior, dark)
        box(f"RadiatorFace{side:+.0f}", (side * 1.30, 0.41, -0.54),
            (0.62, 0.03, 0.16), exterior, metal)

    # Cannon and machine-gun barrels in the wing leading edge.
    for side in (-1.0, 1.0):
        for x, radius, length in ((1.02, 0.035, 0.44), (1.42, 0.030, 0.38)):
            cylinder_between(
                f"Gun{side:+.0f}_{x:.2f}",
                (side * x, 0.86, -0.19),
                (side * x, 0.86 + length, -0.19),
                radius, exterior, metal, vertices=10,
            )

    # Pitot under the left wingtip and a small antenna mast.
    cylinder_between("Pitot", (-4.6, 0.30, -0.34), (-4.6, 1.05, -0.34),
                     0.018, exterior, metal, vertices=8)
    cylinder_between("AntennaMast", (0.0, -1.30, 0.70), (0.0, -1.30, 1.02),
                     0.014, exterior, dark, vertices=8)

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------


def assign_underside(exterior, underside):
    """Give downward-facing airframe polygons the medium-sea-grey underside."""
    for obj in collection_objects(exterior):
        if obj.type != "MESH":
            continue
        names = [slot.material.name if slot.material else "" for slot in obj.material_slots]
        if "airframe" not in names:
            continue
        if underside.name not in names:
            obj.data.materials.append(underside)
            underside_index = len(obj.data.materials) - 1
        else:
            underside_index = names.index(underside.name)

        matrix = obj.matrix_world
        normal_matrix = matrix.to_3x3()
        for polygon in obj.data.polygons:
            center = matrix @ polygon.center
            normal = (normal_matrix @ polygon.normal).normalized()
            if normal.z < -0.22:
                polygon.material_index = underside_index


def consolidate(exterior, cockpit):
    """Merge static meshes into a few draw calls, keeping animated nodes separate."""
    protected = set()
    roots = [
        "Propeller", "AileronL", "AileronR", "FlapL", "FlapR",
        "ElevatorL", "ElevatorR", "Rudder",
        "GearMainL", "GearMainR", "GearTail", "Canopy",
        "FuselageFront", "FuselageCenter", "FuselageRear",
    ]
    for name in roots:
        obj = bpy.data.objects.get(name)
        if obj is not None:
            protected.update(id(part) for part in descendants(obj))

    static = [
        obj for obj in collection_objects(exterior)
        if obj.type == "MESH" and id(obj) not in protected
    ]
    join_meshes("ExteriorStatic", static, exterior)

    cockpit_root = bpy.data.objects.get("CockpitInterior")
    if cockpit_root is not None:
        cockpit_meshes = [obj for obj in descendants(cockpit_root) if obj.type == "MESH"]
        join_meshes("CockpitStatic", cockpit_meshes, cockpit)
        # Soften the box-built interior; the modifier is applied on GLB export
        # and stays visible in the source scene for review.
        cockpit_static = bpy.data.objects.get("CockpitStatic")
        if cockpit_static is not None:
            bevel = cockpit_static.modifiers.new("CockpitBevel", "BEVEL")
            bevel.width = 0.004
            bevel.segments = 2
            bevel.limit_method = "ANGLE"
            bevel.angle_limit = math.radians(40.0)


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    model_dir = os.path.join(root, "game", "assets", "aircraft", "spitfire-mk9", "model")
    source_dir = os.path.join(root, "art", "aircraft", "spitfire-mk9")
    os.makedirs(model_dir, exist_ok=True)
    os.makedirs(source_dir, exist_ok=True)

    reset_scene()
    exterior = new_collection("EXTERIOR")
    cockpit = new_collection("COCKPIT")

    texture_dir = os.path.join(model_dir, "textures")
    mats = {
        "airframe": image_material(
            "airframe",
            os.path.join(texture_dir, "spitfire_camo_albedo.png"),
            os.path.join(texture_dir, "spitfire_camo_normal.png"),
            roughness=0.58,
            metallic=0.05,
            uv_scale=1.0,
            orm_path=os.path.join(texture_dir, "spitfire_camo_orm.png"),
        ),
        "airframe_solid": material("airframe_solid", (0.19, 0.25, 0.16), 0.62, 0.05),
        "metal": material("metal", (0.46, 0.48, 0.46), 0.32, 0.75),
        "spinner": material("spinner", (0.38, 0.46, 0.48), 0.55, 0.10),
        "dark": material("dark", (0.055, 0.060, 0.055), 0.78, 0.15),
        "tyre": material("tyre", (0.025, 0.025, 0.025), 0.95, 0.0),
        "glass": material("glass", (0.28, 0.36, 0.40), 0.05, 0.25, alpha=0.55,
                          double_sided=True),
        "interior": material("interior", (0.16, 0.20, 0.13), 0.80, 0.02),
        "leather": material("leather", (0.20, 0.10, 0.045), 0.88, 0.0),
        "harness": material("harness", (0.25, 0.22, 0.12), 0.92, 0.0),
        "gauge_face": material("gauge_face", (0.055, 0.070, 0.055), 0.55, 0.05),
        "panel_line": material("panel_line", (0.055, 0.060, 0.050), 0.90, 0.02),
        "roundel_blue": material("roundel_blue", (0.075, 0.14, 0.38), 0.62, 0.0,
                                 double_sided=True),
        "roundel_white": material("roundel_white", (0.82, 0.82, 0.78), 0.60, 0.0,
                                  double_sided=True),
        "roundel_red": material("roundel_red", (0.55, 0.035, 0.055), 0.62, 0.0,
                                double_sided=True),
        "underside": material("underside", (0.50, 0.53, 0.54), 0.62, 0.03),
    }

    build_fuselage(exterior, mats["airframe"])
    build_wing(exterior, mats["airframe"])
    build_wing_panel("AileronL", -0.94, -0.56, exterior, mats["airframe"])
    build_wing_panel("AileronR", 0.56, 0.94, exterior, mats["airframe"])
    build_wing_panel("FlapL", -0.52, -0.10, exterior, mats["airframe"])
    build_wing_panel("FlapR", 0.10, 0.52, exterior, mats["airframe"])
    build_tail(exterior, mats["airframe"])
    build_fin(exterior, mats["airframe"])
    build_gear(exterior, mats["metal"], mats["tyre"])
    build_propeller(exterior, mats["spinner"], mats["dark"])
    build_canopy(exterior, mats["glass"], mats["dark"], mats["airframe"])
    build_cockpit(cockpit, mats)
    build_details(exterior, mats)
    build_panel_lines(exterior, mats)
    assign_underside(exterior, mats["underside"])

    # Contract nodes used by the runtime.
    cockpit_root = empty("CockpitInterior", (0.0, 0.0, 0.0), cockpit)
    for obj in collection_objects(cockpit):
        if obj is not cockpit_root:
            parent_keep(obj, cockpit_root)
    empty("DamageSmokeAnchor", (0.0, 2.55, 0.08), exterior)

    consolidate(exterior, cockpit)
    meshes = collection_objects(exterior) + collection_objects(cockpit)
    uv_unwrap_all(meshes)

    # Bake the geometry panel lines into a 4K normal map and drop them from the
    # runtime mesh; the material's normal node now carries the same detail.
    bake_detail_normal(
        bpy.data.objects.get("ExteriorStatic"),
        os.path.join(texture_dir, "spitfire_camo_normal.png"),
        size=4096,
    )
    ao_path = os.path.join(texture_dir, "spitfire_camo_ao.png")
    bake_ao(bpy.data.objects.get("ExteriorStatic"), ao_path, size=4096)
    attach_occlusion(mats["airframe"], ao_path)

    # Metadata for the exporter / reviewer.
    scene = bpy.context.scene
    scene["aircraft_id"] = "spitfire-mk9"
    scene["display_name"] = "Supermarine Spitfire LF Mk IX"
    scene["frame"] = "Blender +Y nose, +Z up, metres, origin at CoG"
    scene["model_variant"] = "LF IX, four-blade prop, framed canopy"

    bpy.context.preferences.filepaths.save_version = 0
    blend_path = os.path.join(source_dir, "spitfire-mk9.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend_path)

    glb_path = os.path.join(model_dir, "spitfire-mk9_lod0.glb")
    objects = collection_objects(exterior) + collection_objects(cockpit)
    export_glb(glb_path, objects)

    print(f"Wrote {glb_path}")
    print(f"Wrote {blend_path}")


if __name__ == "__main__":
    main()
