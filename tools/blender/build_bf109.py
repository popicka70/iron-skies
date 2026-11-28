"""Build a first-pass Messerschmitt Bf 109 G-6 blockout.

Run:

    blender --background --python tools/blender/build_bf109.py

Outputs:

    game/assets/aircraft/bf109-g6/model/bf109-g6_lod0.glb
    art/aircraft/bf109-g6/bf109-g6.blend
"""

from __future__ import annotations

import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from aircraft_common import (  # noqa: E402
    airfoil_loop,
    bake_detail_normal,
    box,
    collection_objects,
    cylinder,
    cylinder_between,
    descendants,
    export_glb,
    image_material,
    join_meshes,
    loft,
    material,
    mesh_object,
    naca_offset,
    new_collection,
    reset_scene,
    rivet_strip,
    superellipse,
    tube_from_points,
    uv_unwrap_all,
)

SPAN = 9.92
SPAN_HALF = SPAN / 2.0
LENGTH = 9.02

WING_ROOT_CHORD = 2.20
WING_TIP_CHORD = 0.55
WING_ROOT_LE = 1.00
WING_TIP_LE = 0.45
WING_ROOT_Z = -0.22
WING_DIHEDRAL = math.radians(6.0)
WING_ROOT_THICKNESS = 0.14
WING_TIP_THICKNESS = 0.10
WING_CAMBER = 0.02

TAIL_ROOT_LE = -3.45
TAIL_ROOT_CHORD = 0.95
TAIL_TIP_CHORD = 0.35
TAIL_TIP_LE = -3.75
TAIL_Z = 0.18
TAIL_SPAN_HALF = 1.50
TAIL_THICKNESS = 0.09

PROP_Y = 3.85
PROP_RADIUS = 1.50

FUSELAGE_STATIONS = [
    (3.80, 0.26, 0.34, -0.38),
    (3.40, 0.34, 0.42, -0.46),
    (2.90, 0.40, 0.48, -0.54),
    (2.30, 0.43, 0.54, -0.62),
    (1.70, 0.44, 0.58, -0.64),
    (1.20, 0.44, 0.62, -0.62),
    (0.60, 0.44, 0.66, -0.60),
    (0.00, 0.43, 0.64, -0.58),
    (-0.60, 0.42, 0.66, -0.52),
    (-1.20, 0.40, 0.64, -0.46),
    (-1.80, 0.37, 0.58, -0.40),
    (-2.40, 0.33, 0.50, -0.34),
    (-3.00, 0.28, 0.42, -0.28),
    (-3.60, 0.22, 0.35, -0.22),
    (-4.20, 0.16, 0.30, -0.16),
    (-4.70, 0.10, 0.26, -0.10),
    (-5.07, 0.03, 0.22, -0.03),
]

FUSELAGE_SEGMENTS = 28


def section_ring(station):
    y, width, z_top, z_bottom = station
    return superellipse(y, width, z_top, z_bottom, count=FUSELAGE_SEGMENTS, exponent=2.05)


def build_fuselage(exterior, mat):
    stations = FUSELAGE_STATIONS

    def rings_between(y_start, y_end):
        selected = [s for s in stations if y_end <= s[0] <= y_start]
        return [section_ring(s) for s in selected]

    loft("FuselageFront", rings_between(3.80, 1.20), exterior, mat,
         cap_start=True, cap_end=False, split_angle_deg=32.0)
    loft("FuselageCenter", rings_between(1.20, -0.70), exterior, mat,
         cap_start=False, cap_end=False, split_angle_deg=32.0)
    loft("FuselageRear", rings_between(-0.70, -5.07), exterior, mat,
         cap_start=False, cap_end=True, split_angle_deg=32.0)


def wing_planform(t):
    a = abs(t)
    chord = WING_ROOT_CHORD - (WING_ROOT_CHORD - WING_TIP_CHORD) * a
    le_y = WING_ROOT_LE - (WING_ROOT_LE - WING_TIP_LE) * a
    z = WING_ROOT_Z + math.tan(WING_DIHEDRAL) * SPAN_HALF * a
    return SPAN_HALF * t, chord, le_y, z


def wing_point(t, chord_fraction, upper):
    x, chord, le_y, z_center = wing_planform(t)
    thickness = WING_ROOT_THICKNESS + (WING_TIP_THICKNESS - WING_ROOT_THICKNESS) * abs(t)
    yc, yt, theta = naca_offset(WING_CAMBER, 0.4, thickness, chord_fraction)
    offset = yc + yt if upper else yc - yt
    dy = -chord_fraction * chord
    dz = offset * chord
    twist = math.radians(-1.5) * (abs(t) ** 1.4)
    return (
        x,
        le_y + dy * math.cos(twist) - dz * math.sin(twist),
        z_center + dy * math.sin(twist) + dz * math.cos(twist),
    )


def build_wing(exterior, mat):
    span_stations = [-1.0 + 2.0 * i / 20.0 for i in range(21)]
    rings = []
    for t in span_stations:
        section = []
        for cx, cz in airfoil_loop(WING_CAMBER, 0.4, 0.12, count=22):
            x, chord, le_y, z_center = wing_planform(t)
            thickness = WING_ROOT_THICKNESS + (WING_TIP_THICKNESS - WING_ROOT_THICKNESS) * abs(t)
            scale = thickness / 0.12
            dy = -cx * chord
            dz = cz * chord * scale
            twist = math.radians(-1.5) * (abs(t) ** 1.4)
            section.append((
                x,
                le_y + dy * math.cos(twist) - dz * math.sin(twist),
                z_center + dy * math.sin(twist) + dz * math.cos(twist),
            ))
        rings.append(section)
    loft("Wing", rings, exterior, mat, cap_start=True, cap_end=True,
         split_angle_deg=32.0)


def build_wing_panel(name, t0, t1, exterior, mat, chord0=0.74, chord1=1.0):
    t_mid = 0.5 * (t0 + t1)
    x_mid, chord_mid, le_mid, z_mid = wing_planform(t_mid)
    hinge = (x_mid, le_mid - chord0 * chord_mid, z_mid)
    rings = []
    for i in range(9):
        t = t0 + (t1 - t0) * i / 8.0
        upper0 = wing_point(t, chord0, True)
        upper1 = wing_point(t, chord1, True)
        lower1 = wing_point(t, chord1, False)
        lower0 = wing_point(t, chord0, False)
        eps = 0.006
        rings.append([
            (upper0[0] - hinge[0], upper0[1] - hinge[1], upper0[2] - hinge[2] + eps),
            (upper1[0] - hinge[0], upper1[1] - hinge[1], upper1[2] - hinge[2] + eps),
            (lower1[0] - hinge[0], lower1[1] - hinge[1], lower1[2] - hinge[2] - eps),
            (lower0[0] - hinge[0], lower0[1] - hinge[1], lower0[2] - hinge[2] - eps),
        ])
    obj = loft(name, rings, exterior, mat, cap_start=True, cap_end=True,
               split_angle_deg=32.0)
    obj.location = hinge


def build_tail(exterior, mat):
    rings = []
    for i in range(17):
        t = -1.0 + 2.0 * i / 16.0
        a = abs(t)
        x = TAIL_SPAN_HALF * t
        chord = TAIL_ROOT_CHORD - (TAIL_ROOT_CHORD - TAIL_TIP_CHORD) * a
        le_y = TAIL_ROOT_LE - (TAIL_ROOT_LE - TAIL_TIP_LE) * a
        section = []
        for cx, cz in airfoil_loop(0.0, 0.4, TAIL_THICKNESS, count=16):
            section.append((x, le_y - cx * chord, TAIL_Z + cz * chord))
        rings.append(section)
    loft("Tailplane", rings, exterior, mat, cap_start=True, cap_end=True,
         split_angle_deg=32.0)

    # Elevators.
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        t0, t1 = side * 0.08, side * 0.95
        t_mid = 0.5 * (t0 + t1)
        a = abs(t_mid)
        chord_mid = TAIL_ROOT_CHORD - (TAIL_ROOT_CHORD - TAIL_TIP_CHORD) * a
        le_mid = TAIL_ROOT_LE - (TAIL_ROOT_LE - TAIL_TIP_LE) * a
        hinge = (TAIL_SPAN_HALF * t_mid, le_mid - 0.72 * chord_mid, TAIL_Z)
        rings = []
        for i in range(7):
            t = t0 + (t1 - t0) * i / 6.0
            aa = abs(t)
            chord = TAIL_ROOT_CHORD - (TAIL_ROOT_CHORD - TAIL_TIP_CHORD) * aa
            le_y = TAIL_ROOT_LE - (TAIL_ROOT_LE - TAIL_TIP_LE) * aa
            x = TAIL_SPAN_HALF * t
            dz = TAIL_THICKNESS * chord * 0.05
            rings.append([
                (x - hinge[0], le_y - 0.72 * chord - hinge[1], TAIL_Z + dz - hinge[2]),
                (x - hinge[0], le_y - chord - hinge[1], TAIL_Z - hinge[2]),
                (x - hinge[0], le_y - chord - hinge[1], TAIL_Z - hinge[2]),
                (x - hinge[0], le_y - 0.72 * chord - hinge[1], TAIL_Z - dz - hinge[2]),
            ])
        obj = loft(f"Elevator{suffix}", rings, exterior, mat, cap_start=True, cap_end=True,
                   split_angle_deg=32.0)
        obj.location = hinge

    # Struts.
    for side in (-1.0, 1.0):
        cylinder_between(f"TailStrut{side:+.0f}",
                         (side * 0.30, -3.42, 0.02),
                         (side * 0.68, -3.78, TAIL_Z),
                         0.025, exterior, mat, vertices=10)


def build_fin(exterior, mat):
    stations = [
        (0.08, -3.30, 1.23, 0.085),
        (0.30, -3.36, 1.18, 0.085),
        (0.55, -3.46, 1.09, 0.080),
        (0.80, -3.60, 0.96, 0.078),
        (1.02, -3.76, 0.80, 0.072),
        (1.18, -3.92, 0.64, 0.066),
        (1.30, -4.06, 0.48, 0.060),
        (1.38, -4.18, 0.33, 0.052),
        (1.42, -4.28, 0.21, 0.045),
    ]
    rings = []
    for z, le_y, chord, thickness in stations:
        ring = []
        for cx, cz in airfoil_loop(0.0, 0.4, thickness, count=18):
            ring.append((cz * chord, le_y - cx * chord, z))
        rings.append(ring)
    loft("Fin", rings, exterior, mat, cap_start=True, cap_end=True, split_angle_deg=32.0)

    rudder_outline = [
        (-4.30, 0.12), (-4.32, 0.36), (-4.34, 0.62), (-4.36, 0.90),
        (-4.38, 1.12), (-4.46, 1.26), (-4.56, 1.18), (-4.62, 0.92),
        (-4.64, 0.62), (-4.62, 0.34), (-4.58, 0.12),
    ]
    hinge_y, hinge_z = -4.30, 0.62
    half = 0.035
    verts = [(-half, y - hinge_y, z - hinge_z) for y, z in rudder_outline]
    verts += [(half, y - hinge_y, z - hinge_z) for y, z in rudder_outline]
    n = len(rudder_outline)
    faces = [(i, (i + 1) % n, n + (i + 1) % n, n + i) for i in range(n)]
    faces.append(tuple(range(n - 1, -1, -1)))
    faces.append(tuple(range(n, 2 * n)))
    rudder = mesh_object("Rudder", verts, faces, exterior, mat, smooth=False)
    rudder.location = (0.0, hinge_y, hinge_z)


def empty(name, location, collection):
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
        pivot = empty(f"GearMain{suffix}", (side * 1.20, 0.90, -0.40), exterior)
        leg = cylinder_between(
            f"GearLeg{suffix}",
            (side * 1.20, 0.90, -0.40),
            (side * 1.30, 0.90, -1.17),
            0.060, exterior, metal,
        )
        wheel = cylinder(
            f"GearWheel{suffix}", (side * 1.32, 0.90, -1.17),
            0.33, 0.18, exterior, tyre, axis=(1.0, 0.0, 0.0), vertices=24,
        )
        hub = cylinder(
            f"GearHub{suffix}", (side * 1.32, 0.90, -1.17),
            0.11, 0.19, exterior, metal, axis=(1.0, 0.0, 0.0), vertices=16,
        )
        door = box(f"GearDoor{suffix}", (side * 1.37, 0.90, -0.78),
                   (0.035, 0.48, 0.50), exterior, metal)
        for part in (leg, wheel, hub, door):
            parent_keep(part, pivot)

    tail_pivot = empty("GearTail", (0.0, -4.40, -0.20), exterior)
    tail_leg = cylinder_between("GearTailLeg", (0.0, -4.40, -0.20),
                                (0.0, -4.50, -0.28), 0.040, exterior, metal)
    tail_wheel = cylinder("GearTailWheel", (0.0, -4.50, -0.28),
                          0.14, 0.09, exterior, tyre, axis=(1.0, 0.0, 0.0), vertices=18)
    for part in (tail_leg, tail_wheel):
        parent_keep(part, tail_pivot)


def build_propeller(exterior, metal, dark):
    rings = []
    for y, radius in ((3.72, 0.32), (3.82, 0.30), (3.90, 0.22), (3.96, 0.10), (4.00, 0.012)):
        rings.append([
            (radius * math.cos(2.0 * math.pi * i / 28.0), y,
             radius * math.sin(2.0 * math.pi * i / 28.0))
            for i in range(28)
        ])
    loft("Spinner", rings, exterior, metal, cap_start=True, cap_end=True,
         split_angle_deg=45.0)

    prop = empty("Propeller", (0.0, PROP_Y, 0.0), exterior)
    all_verts = []
    all_faces = []
    for blade in range(3):
        angle = 2.0 * math.pi * blade / 3.0
        ca, sa = math.cos(angle), math.sin(angle)
        stations = [0.26, 0.48, 0.78, 1.08, 1.32, 1.46, 1.50]
        ring_indices = []
        for r in stations:
            chord = 0.18 + 0.14 * math.sin(math.pi * min(1.0, (r - 0.26) / 1.24))
            pitch = math.radians(46.0 - 26.0 * (r - 0.26) / 1.24)
            section = airfoil_loop(0.04, 0.4, 0.12, count=14)
            start = len(all_verts)
            for cx, cz in section:
                z_local = (cx - 0.35) * chord
                y_local = cz * chord
                y_rot = y_local * math.cos(pitch) - z_local * math.sin(pitch)
                z_rot = y_local * math.sin(pitch) + z_local * math.cos(pitch)
                all_verts.append((r * ca - z_rot * sa, y_rot, r * sa + z_rot * ca))
            ring_indices.append((start, len(section)))
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
    root = cylinder("PropellerRoot", (0.0, PROP_Y, 0.0), 0.17, 0.20, exterior, dark,
                    axis=(0.0, 1.0, 0.0), vertices=20)
    parent_keep(root, prop)


def canopy_ring(y, width, top, base=0.64, count=19, exponent=2.0):
    power = 2.0 / exponent
    ring = []
    for i in range(count):
        theta = math.pi * i / (count - 1)
        c, s = math.cos(theta), math.sin(theta)
        x = width * math.copysign(abs(c) ** power, c)
        z = base + (top - base) * (abs(s) ** power)
        ring.append((x, y, z))
    return ring


def build_canopy(exterior, glass, frame, airframe):
    stations = [
        (1.22, 0.26, 0.88), (1.10, 0.32, 0.94), (0.88, 0.35, 0.97),
        (0.52, 0.36, 0.98), (0.12, 0.35, 0.97), (-0.28, 0.32, 0.94),
        (-0.60, 0.27, 0.89), (-0.80, 0.19, 0.82),
    ]
    count = 19
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
        exterior, glass, smooth=True, split_angle_deg=55.0,
    )

    for index, radius in ((0, 0.022), (2, 0.020), (4, 0.018), (6, 0.018), (7, 0.022)):
        y, width, top = stations[index]
        points = [
            (width * math.cos(math.pi * i / 12.0), y,
             0.64 + (top - 0.64) * math.sin(math.pi * i / 12.0))
            for i in range(13)
        ]
        tube_from_points(f"CanopyFrame{index}", points, radius, exterior, frame)

    for side in (-1.0, 1.0):
        points = [(side * width, y, 0.64) for y, width, _ in stations]
        tube_from_points(f"CanopyRail{'L' if side < 0 else 'R'}", points, 0.017,
                         exterior, frame)
    spine = [(0.0, y, top) for y, _, top in stations]
    tube_from_points("CanopySpine", spine, 0.015, exterior, frame)

    # Razorback spine fairing behind the canopy.
    fairing = [
        (-0.72, 0.21, 0.66, 0.40),
        (-0.95, 0.19, 0.64, 0.41),
        (-1.25, 0.16, 0.61, 0.42),
        (-1.55, 0.12, 0.57, 0.43),
        (-1.82, 0.07, 0.53, 0.44),
    ]
    rings = [superellipse(y, w, top, bottom, count=16, exponent=2.0)
             for y, w, top, bottom in fairing]
    loft("RazorbackFairing", rings, exterior, airframe, cap_start=True, cap_end=True,
         split_angle_deg=40.0)


def build_cockpit(cockpit, mats):
    interior = mats["interior"]
    dark = mats["dark"]
    metal = mats["metal"]
    leather = mats["leather"]
    face = mats["gauge_face"]
    harness = mats["harness"]

    box("CockpitFloor", (0.0, -0.05, -0.08), (0.76, 1.55, 0.05), cockpit, interior)
    box("CockpitWallL", (-0.39, -0.05, 0.25), (0.05, 1.55, 0.62), cockpit, interior)
    box("CockpitWallR", (0.39, -0.05, 0.25), (0.05, 1.55, 0.62), cockpit, interior)
    box("CockpitRear", (0.0, -0.80, 0.25), (0.80, 0.06, 0.64), cockpit, interior)
    box("CockpitFront", (0.0, 0.76, 0.25), (0.80, 0.06, 0.64), cockpit, interior)

    # Coaming, sill rails and sidewall ribs.
    box("CoamingFront", (0.0, 0.76, 0.60), (0.84, 0.10, 0.07), cockpit, leather,
        rotation=(math.radians(-8.0), 0.0, 0.0))
    box("CoamingL", (-0.41, -0.05, 0.60), (0.07, 1.60, 0.06), cockpit, leather)
    box("CoamingR", (0.41, -0.05, 0.60), (0.07, 1.60, 0.06), cockpit, leather)
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        box(f"CanopySill{suffix}", (side * 0.375, -0.05, 0.575),
            (0.03, 1.40, 0.022), cockpit, metal)
        for index, y in enumerate((-0.62, -0.30, 0.02, 0.34, 0.62)):
            box(f"SidewallRib{suffix}{index}", (side * 0.395, y, 0.28),
                (0.022, 0.04, 0.44), cockpit, interior)

    # Three-section panel with two rows of four gauges, switches and compass.
    tilt = math.radians(-8.0)
    box("InstrumentPanel", (0.0, 0.67, 0.43), (0.48, 0.06, 0.40), cockpit, dark,
        rotation=(tilt, 0.0, 0.0))
    box("PanelFace", (0.0, 0.638, 0.43), (0.44, 0.02, 0.34), cockpit, face,
        rotation=(tilt, 0.0, 0.0))
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        box(f"PanelSide{suffix}", (side * 0.30, 0.68, 0.43),
            (0.20, 0.05, 0.32), cockpit, dark,
            rotation=(tilt, 0.0, side * math.radians(24.0)))
        box(f"SwitchBlock{suffix}", (side * 0.30, 0.651, 0.40),
            (0.13, 0.02, 0.14), cockpit, face,
            rotation=(tilt, 0.0, side * math.radians(24.0)))
        for index in range(3):
            cylinder(f"Switch{suffix}{index}",
                     (side * (0.26 + 0.04 * index), 0.642, 0.415),
                     0.007, 0.03, cockpit, metal, axis=(0.0, 1.0, 0.0),
                     vertices=10)
    box("GlareShield", (0.0, 0.69, 0.635), (0.82, 0.14, 0.03), cockpit, leather,
        rotation=(math.radians(-14.0), 0.0, 0.0))

    for i in range(8):
        x = (-1.5 + (i % 4)) * 0.095
        z = 0.515 - (i // 4) * 0.115
        cylinder(f"Gauge{i}", (x, 0.628, z), 0.044, 0.018, cockpit, metal,
                 axis=(0.0, 1.0, 0.0), vertices=18)
        cylinder(f"GaugeFace{i}", (x, 0.620, z), 0.035, 0.016, cockpit, face,
                 axis=(0.0, 1.0, 0.0), vertices=18)
    cylinder("Compass", (0.0, 0.624, 0.60), 0.025, 0.02, cockpit, dark,
             axis=(0.0, 1.0, 0.0), vertices=14)

    # Seat, cushions, armour, headrest and harness.
    box("SeatPan", (0.0, -0.28, 0.02), (0.40, 0.38, 0.08), cockpit, leather)
    box("SeatCushion", (0.0, -0.26, 0.07), (0.34, 0.30, 0.04), cockpit, leather)
    box("SeatBack", (0.0, -0.50, 0.30), (0.40, 0.09, 0.50), cockpit, leather)
    box("BackCushion", (0.0, -0.455, 0.32), (0.32, 0.04, 0.38), cockpit, leather)
    box("SeatArmour", (0.0, -0.57, 0.32), (0.44, 0.04, 0.56), cockpit, dark)
    box("Headrest", (0.0, -0.515, 0.60), (0.17, 0.09, 0.12), cockpit, leather)
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        box(f"Harness{suffix}", (side * 0.10, -0.44, 0.35),
            (0.045, 0.028, 0.40), cockpit, harness,
            rotation=(math.radians(7.0), 0.0, 0.0))
        box(f"HarnessBuckle{suffix}", (side * 0.10, -0.405, 0.17),
            (0.065, 0.04, 0.05), cockpit, metal)
    box("LapBelt", (0.0, -0.35, 0.10), (0.32, 0.045, 0.04), cockpit, harness)

    # Control column, grip, boot and pedals.
    cylinder_between("ControlColumn", (0.0, 0.38, -0.05), (0.0, 0.33, 0.25),
                     0.025, cockpit, metal)
    box("ControlBoot", (0.0, 0.38, -0.01), (0.10, 0.12, 0.07), cockpit, dark)
    box("ControlGrip", (0.0, 0.325, 0.295), (0.075, 0.10, 0.10), cockpit, dark)
    cylinder("ControlButton", (0.0, 0.31, 0.345), 0.011, 0.018, cockpit, metal,
             axis=(0.0, 0.0, 1.0), vertices=10)
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        box(f"Pedal{suffix}", (side * 0.15, 0.68, -0.035),
            (0.14, 0.05, 0.18), cockpit, metal,
            rotation=(math.radians(18.0), 0.0, 0.0))

    # Throttle and mixture on the left wall.
    box("ThrottleBase", (-0.31, 0.42, 0.18), (0.09, 0.30, 0.07), cockpit, dark)
    cylinder_between("ThrottleLever", (-0.31, 0.44, 0.20), (-0.31, 0.36, 0.34),
                     0.018, cockpit, metal)
    box("ThrottleGrip", (-0.31, 0.345, 0.365), (0.04, 0.07, 0.04), cockpit, dark)
    box("MixtureBase", (-0.32, 0.28, 0.17), (0.06, 0.14, 0.05), cockpit, dark)
    box("MixtureGrip", (-0.32, 0.24, 0.23), (0.04, 0.055, 0.03), cockpit, dark)

    # Revi gunsight with a sun shade.
    box("ReviBase", (0.0, 0.57, 0.57), (0.11, 0.12, 0.08), cockpit, dark)
    box("ReviArm", (0.0, 0.56, 0.615), (0.045, 0.05, 0.07), cockpit, metal)
    box("ReviGlass", (0.0, 0.55, 0.67), (0.14, 0.01, 0.12), cockpit,
        mats["glass"])
    box("ReviShade", (0.0, 0.535, 0.705), (0.16, 0.08, 0.022), cockpit, dark)

    box("MapPocket", (0.355, -0.36, 0.22), (0.018, 0.22, 0.18), cockpit, interior)


def fuselage_station_at(y):
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
            return (w0 + (w1 - w0) * f, t0 + (t1 - t0) * f, b0 + (b1 - b0) * f)
    raise ValueError(y)


def fuselage_surface_point(y, angle, offset=1.003):
    width, z_top, z_bottom = fuselage_station_at(y)
    z_center = 0.5 * (z_top + z_bottom)
    z_radius = 0.5 * (z_top - z_bottom)
    power = 2.0 / 2.05
    c, s = math.cos(angle), math.sin(angle)
    x = width * offset * math.copysign(abs(c) ** power, c)
    z = z_center + z_radius * offset * math.copysign(abs(s) ** power, s)
    return x, y, z


def fuselage_side_x(y, z, side, offset=0.008):
    width, z_top, z_bottom = fuselage_station_at(y)
    z_center = 0.5 * (z_top + z_bottom)
    z_radius = max(1e-6, 0.5 * (z_top - z_bottom))
    t = min(1.0, abs((z - z_center) / z_radius))
    x = width * (max(0.0, 1.0 - t**2.05) ** (1.0 / 2.05))
    return side * (x + offset)


def build_cross_fuselage(name, y, z, side, exterior, mats):
    x = fuselage_side_x(y, z, side)
    white = mats["cross_white"]
    black = mats["cross_black"]
    # White outline then black centre, both thin plates in the YZ plane.
    box(f"{name}_white_h", (x, y, z), (0.012, 0.56, 0.14), exterior, white)
    box(f"{name}_white_v", (x, y, z), (0.012, 0.14, 0.56), exterior, white)
    xb = x + side * 0.004
    box(f"{name}_black_h", (xb, y, z), (0.012, 0.40, 0.075), exterior, black)
    box(f"{name}_black_v", (xb, y, z), (0.012, 0.075, 0.40), exterior, black)


def build_cross_wing(name, center_x, center_y, upper, exterior, mats):
    _, chord, le_y, _ = wing_planform(center_x / SPAN_HALF)
    c = max(0.0, min(1.0, (le_y - center_y) / max(chord, 1e-6)))
    z = wing_point(center_x / SPAN_HALF, c, upper)[2] + (0.012 if upper else -0.012)
    white = mats["cross_white"]
    black = mats["cross_black"]
    box(f"{name}_white_h", (center_x, center_y, z), (0.66, 0.14, 0.010), exterior, white)
    box(f"{name}_white_v", (center_x, center_y, z), (0.14, 0.66, 0.010), exterior, white)
    zb = z + (0.004 if upper else -0.004)
    box(f"{name}_black_h", (center_x, center_y, zb), (0.46, 0.075, 0.010), exterior, black)
    box(f"{name}_black_v", (center_x, center_y, zb), (0.075, 0.46, 0.010), exterior, black)


def build_details(exterior, mats):
    metal = mats["metal"]
    dark = mats["dark"]

    # MG 131 cowl bulges.
    for side in (-1.0, 1.0):
        stations = [(2.30, 0.05), (2.55, 0.105), (2.95, 0.105), (3.20, 0.05)]
        rings = []
        for y, width in stations:
            ring = superellipse(y, width, 0.50, 0.35, count=12, exponent=2.6)
            rings.append([(x + side * 0.22, yy, zz) for x, yy, zz in ring])
        loft(f"CowlBulge{side:+.0f}", rings, exterior, mats["airframe"],
             cap_start=True, cap_end=True, split_angle_deg=40.0)

    # Exhaust stacks, six per side.
    for side in (-1.0, 1.0):
        for i in range(6):
            y = 3.35 - i * 0.14
            box(f"Exhaust{side:+.0f}_{i}", (side * 0.44, y, 0.02),
                (0.08, 0.13, 0.10), exterior, dark)

    # Supercharger intake on the left side of the nose.
    box("SuperchargerIntake", (-0.44, 2.45, 0.18), (0.18, 0.42, 0.18), exterior, dark)
    cylinder("SuperchargerMouth", (-0.53, 2.62, 0.18), 0.10, 0.04, exterior, dark,
             axis=(0.0, 1.0, 0.0), vertices=16)

    # Under-nose oil cooler.
    box("OilCooler", (0.0, 2.55, -0.48), (0.34, 0.48, 0.20), exterior, dark)
    box("OilCoolerFace", (0.0, 2.79, -0.48), (0.26, 0.03, 0.14), exterior, metal)

    # Underwing radiators.
    for side in (-1.0, 1.0):
        box(f"Radiator{side:+.0f}", (side * 1.28, -0.10, -0.52),
            (0.62, 0.92, 0.22), exterior, dark)
        box(f"RadiatorFace{side:+.0f}", (side * 1.28, 0.36, -0.52),
            (0.52, 0.03, 0.15), exterior, metal)

    # Wing cannon and cowl machine guns.
    for side in (-1.0, 1.0):
        cylinder_between(f"Cannon{side:+.0f}", (side * 1.48, 0.95, -0.16),
                         (side * 1.48, 1.34, -0.16), 0.033, exterior, metal, vertices=10)
        cylinder_between(f"Mg131{side:+.0f}", (side * 0.22, 3.68, 0.28),
                         (side * 0.22, 3.86, 0.28), 0.030, exterior, metal, vertices=10)

    cylinder_between("Pitot", (-4.0, 0.55, -0.30), (-4.0, 1.20, -0.30),
                     0.016, exterior, metal, vertices=8)
    cylinder_between("AntennaMast", (0.0, -1.35, 0.66), (0.0, -1.35, 0.98),
                     0.013, exterior, dark, vertices=8)

    # Leading-edge slats, suggested with a thin line.
    for side in (-1.0, 1.0):
        points = []
        for i in range(8):
            t = side * (0.55 + 0.35 * i / 7.0)
            points.append(wing_point(t, 0.015, True))
        tube_from_points(f"Slat{side:+.0f}", points, 0.010, exterior, dark, resolution=0)


def build_panel_lines(exterior, mats):
    line = mats["panel_line"]
    for y in (3.50, 3.00, 2.50, 2.00, 1.50, 1.05, 0.50, -0.10, -0.70,
              -1.30, -1.90, -2.50, -3.10, -3.70, -4.30):
        points = [fuselage_surface_point(y, 2.0 * math.pi * i / 24.0) for i in range(24)]
        tube_from_points(f"PanelFrame{y:.1f}", points, 0.012, exterior, line, resolution=0)
        rivet_strip(f"RivetsFrame{y:.1f}", points, exterior, line)

    y_values = [3.70 - 0.18 * i for i in range(46)]
    for angle in (0.0, math.pi * 0.5, math.pi, math.pi * 1.5,
                  math.pi * 0.25, math.pi * 0.75, math.pi * 1.25, math.pi * 1.75):
        points = [fuselage_surface_point(y, angle) for y in y_values]
        tube_from_points(f"PanelLongitude{angle:.2f}", points, 0.010, exterior, line,
                         resolution=0)
        rivet_strip(f"RivetsLongitude{angle:.2f}", points, exterior, line)

    for t in (-0.90, -0.70, -0.50, -0.30, -0.10, 0.10, 0.30, 0.50, 0.70, 0.90):
        for upper in (True, False):
            points = [wing_point(t, c / 10.0, upper) for c in range(1, 10)]
            tube_from_points(f"WingRib{t:+.2f}{upper}", points, 0.010, exterior, line,
                             resolution=0)
            rivet_strip(f"RivetsWingRib{t:+.2f}{upper}", points, exterior, line)
    for c in (0.22, 0.52, 0.80):
        for upper in (True, False):
            points = [wing_point(-0.94 + 1.88 * i / 14.0, c, upper) for i in range(15)]
            tube_from_points(f"WingSpan{c:.2f}{upper}", points, 0.010, exterior, line,
                             resolution=0)
            rivet_strip(f"RivetsWingSpan{c:.2f}{upper}", points, exterior, line)

    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        build_cross_fuselage(f"BalkenkreuzFuselage{suffix}", -0.95, 0.12, side,
                             exterior, mats)
        build_cross_wing(f"BalkenkreuzWingTop{suffix}", side * 2.75, 0.28, True,
                         exterior, mats)
        build_cross_wing(f"BalkenkreuzWingBottom{suffix}", side * 2.75, 0.28, False,
                         exterior, mats)


def assign_underside(exterior, underside):
    for obj in collection_objects(exterior):
        if obj.type != "MESH":
            continue
        names = [slot.material.name if slot.material else "" for slot in obj.material_slots]
        if "airframe" not in names:
            continue
        if underside.name not in names:
            obj.data.materials.append(underside)
            index = len(obj.data.materials) - 1
        else:
            index = names.index(underside.name)
        matrix = obj.matrix_world
        normal_matrix = matrix.to_3x3()
        for polygon in obj.data.polygons:
            normal = (normal_matrix @ polygon.normal).normalized()
            if normal.z < -0.22:
                polygon.material_index = index


def consolidate(exterior, cockpit):
    protected = set()
    roots = ["Propeller", "AileronL", "AileronR", "FlapL", "FlapR",
             "ElevatorL", "ElevatorR", "Rudder", "GearMainL", "GearMainR",
             "GearTail", "Canopy", "FuselageFront", "FuselageCenter", "FuselageRear"]
    for name in roots:
        obj = bpy.data.objects.get(name)
        if obj is not None:
            protected.update(id(part) for part in descendants(obj))
    static = [obj for obj in collection_objects(exterior)
              if obj.type == "MESH" and id(obj) not in protected]
    join_meshes("ExteriorStatic", static, exterior)

    cockpit_root = bpy.data.objects.get("CockpitInterior")
    if cockpit_root is not None:
        cockpit_meshes = [obj for obj in descendants(cockpit_root) if obj.type == "MESH"]
        join_meshes("CockpitStatic", cockpit_meshes, cockpit)
        cockpit_static = bpy.data.objects.get("CockpitStatic")
        if cockpit_static is not None:
            bevel = cockpit_static.modifiers.new("CockpitBevel", "BEVEL")
            bevel.width = 0.004
            bevel.segments = 2
            bevel.limit_method = "ANGLE"
            bevel.angle_limit = math.radians(40.0)


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    model_dir = os.path.join(root, "game", "assets", "aircraft", "bf109-g6", "model")
    source_dir = os.path.join(root, "art", "aircraft", "bf109-g6")
    os.makedirs(model_dir, exist_ok=True)
    os.makedirs(source_dir, exist_ok=True)

    reset_scene()
    exterior = new_collection("EXTERIOR")
    cockpit = new_collection("COCKPIT")

    texture_dir = os.path.join(model_dir, "textures")
    mats = {
        "airframe": image_material(
            "airframe",
            os.path.join(texture_dir, "bf109_camo_albedo.png"),
            os.path.join(texture_dir, "bf109_camo_normal.png"),
            roughness=0.58, metallic=0.05, uv_scale=0.16,
        ),
        "underside": material("underside", (0.52, 0.60, 0.64), 0.62, 0.03),
        "metal": material("metal", (0.44, 0.46, 0.45), 0.32, 0.72),
        "dark": material("dark", (0.040, 0.045, 0.040), 0.82, 0.10),
        "tyre": material("tyre", (0.025, 0.025, 0.025), 0.95, 0.0),
        "glass": material("glass", (0.26, 0.33, 0.36), 0.06, 0.25, alpha=0.55,
                          double_sided=True),
        "interior": material("interior", (0.085, 0.095, 0.085), 0.85, 0.02),
        "leather": material("leather", (0.10, 0.07, 0.045), 0.90, 0.0),
        "harness": material("harness", (0.22, 0.19, 0.11), 0.92, 0.0),
        "gauge_face": material("gauge_face", (0.045, 0.055, 0.045), 0.55, 0.05),
        "panel_line": material("panel_line", (0.035, 0.040, 0.035), 0.90, 0.02),
        "cross_white": material("cross_white", (0.82, 0.82, 0.78), 0.62, 0.0,
                                double_sided=True),
        "cross_black": material("cross_black", (0.015, 0.015, 0.015), 0.70, 0.0,
                                double_sided=True),
        "spinner": material("spinner", (0.12, 0.145, 0.10), 0.55, 0.08),
    }

    build_fuselage(exterior, mats["airframe"])
    build_wing(exterior, mats["airframe"])
    build_wing_panel("AileronL", -0.94, -0.56, exterior, mats["airframe"])
    build_wing_panel("AileronR", 0.56, 0.94, exterior, mats["airframe"])
    build_wing_panel("FlapL", -0.50, -0.08, exterior, mats["airframe"])
    build_wing_panel("FlapR", 0.08, 0.50, exterior, mats["airframe"])
    build_tail(exterior, mats["airframe"])
    build_fin(exterior, mats["airframe"])
    build_gear(exterior, mats["metal"], mats["tyre"])
    build_propeller(exterior, mats["spinner"], mats["dark"])
    build_canopy(exterior, mats["glass"], mats["dark"], mats["airframe"])
    build_cockpit(cockpit, mats)
    build_details(exterior, mats)
    build_panel_lines(exterior, mats)
    assign_underside(exterior, mats["underside"])

    cockpit_root = empty("CockpitInterior", (0.0, 0.0, 0.0), cockpit)
    for obj in collection_objects(cockpit):
        if obj is not cockpit_root:
            parent_keep(obj, cockpit_root)
    empty("DamageSmokeAnchor", (0.0, 2.55, 0.06), exterior)

    consolidate(exterior, cockpit)
    meshes = collection_objects(exterior) + collection_objects(cockpit)
    uv_unwrap_all(meshes)

    bake_detail_normal(
        bpy.data.objects.get("ExteriorStatic"),
        os.path.join(texture_dir, "bf109_camo_normal.png"),
        size=4096,
        ignore_materials=("cross_white", "cross_black"),
    )

    scene = bpy.context.scene
    scene["aircraft_id"] = "bf109-g6"
    scene["display_name"] = "Messerschmitt Bf 109 G-6"
    scene["frame"] = "Blender +Y nose, +Z up, metres, origin at CoG"
    scene["model_variant"] = "G-6, Erla canopy, MG 131 cowl bulges, 3-blade prop"

    bpy.context.preferences.filepaths.save_version = 0
    blend_path = os.path.join(source_dir, "bf109-g6.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend_path)

    glb_path = os.path.join(model_dir, "bf109-g6_lod0.glb")
    export_glb(glb_path, meshes)
    print(f"Wrote {glb_path}")
    print(f"Wrote {blend_path}")


if __name__ == "__main__":
    main()
