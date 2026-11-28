"""Build a first-pass Junkers Ju 88 A-4 blockout.

AI-only bomber: twin Jumo 211 nacelles, glazed greenhouse nose, broad wing,
ventral Bola gondola and dive brakes.

Run:

    blender --background --python tools/blender/build_ju88.py
"""

from __future__ import annotations

import math
import os
import sys

import bpy

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

SPAN = 20.10
SPAN_HALF = SPAN / 2.0
LENGTH = 14.36

WING_ROOT_CHORD = 3.80
WING_TIP_CHORD = 1.20
WING_ROOT_LE = 2.50
WING_TIP_LE = 0.70
WING_ROOT_Z = -0.15
WING_DIHEDRAL = math.radians(5.0)
WING_ROOT_THICKNESS = 0.15
WING_TIP_THICKNESS = 0.10

NACELLE_X = 3.20
PROP_Y = 5.00
PROP_RADIUS = 1.68

FUSELAGE_STATIONS = [
    (7.18, 0.30, 0.55, -0.35),
    (6.80, 0.55, 0.75, -0.50),
    (6.20, 0.75, 0.95, -0.60),
    (5.40, 0.85, 1.05, -0.68),
    (4.60, 0.90, 1.05, -0.72),
    (3.60, 0.92, 1.00, -0.75),
    (2.50, 0.92, 0.92, -0.78),
    (1.20, 0.90, 0.85, -0.80),
    (0.00, 0.88, 0.80, -0.80),
    (-1.20, 0.84, 0.76, -0.76),
    (-2.40, 0.76, 0.70, -0.70),
    (-3.60, 0.64, 0.64, -0.60),
    (-4.80, 0.50, 0.56, -0.48),
    (-5.80, 0.34, 0.48, -0.34),
    (-6.60, 0.20, 0.40, -0.22),
    (-7.18, 0.06, 0.30, -0.10),
]

FUSELAGE_SEGMENTS = 28


def section_ring(station):
    y, width, z_top, z_bottom = station
    return superellipse(y, width, z_top, z_bottom, count=FUSELAGE_SEGMENTS, exponent=2.25)


def build_fuselage(exterior, mat):
    loft("Fuselage", [section_ring(s) for s in FUSELAGE_STATIONS], exterior, mat,
         cap_start=True, cap_end=True, split_angle_deg=32.0)


def wing_planform(t):
    a = abs(t)
    chord = WING_ROOT_CHORD - (WING_ROOT_CHORD - WING_TIP_CHORD) * a
    le_y = WING_ROOT_LE - (WING_ROOT_LE - WING_TIP_LE) * a
    z = WING_ROOT_Z + math.tan(WING_DIHEDRAL) * SPAN_HALF * a
    return SPAN_HALF * t, chord, le_y, z


def wing_point(t, chord_fraction, upper):
    x, chord, le_y, z_center = wing_planform(t)
    thickness = WING_ROOT_THICKNESS + (WING_TIP_THICKNESS - WING_ROOT_THICKNESS) * abs(t)
    yc, yt, theta = naca_offset(0.02, 0.4, thickness, chord_fraction)
    offset = yc + yt if upper else yc - yt
    dy = -chord_fraction * chord
    dz = offset * chord
    twist = math.radians(-1.0) * (abs(t) ** 1.5)
    return (
        x,
        le_y + dy * math.cos(twist) - dz * math.sin(twist),
        z_center + dy * math.sin(twist) + dz * math.cos(twist),
    )


def build_wing(exterior, mat):
    rings = []
    for i in range(23):
        t = -1.0 + 2.0 * i / 22.0
        section = []
        for cx, cz in airfoil_loop(0.02, 0.4, 0.13, count=24):
            x, chord, le_y, z_center = wing_planform(t)
            thickness = WING_ROOT_THICKNESS + (WING_TIP_THICKNESS - WING_ROOT_THICKNESS) * abs(t)
            scale = thickness / 0.13
            dy = -cx * chord
            dz = cz * chord * scale
            twist = math.radians(-1.0) * (abs(t) ** 1.5)
            section.append((
                x,
                le_y + dy * math.cos(twist) - dz * math.sin(twist),
                z_center + dy * math.sin(twist) + dz * math.cos(twist),
            ))
        rings.append(section)
    loft("Wing", rings, exterior, mat, cap_start=True, cap_end=True, split_angle_deg=32.0)


def build_wing_panel(name, t0, t1, exterior, mat, chord0=0.76, chord1=1.0):
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
        eps = 0.007
        rings.append([
            (upper0[0] - hinge[0], upper0[1] - hinge[1], upper0[2] - hinge[2] + eps),
            (upper1[0] - hinge[0], upper1[1] - hinge[1], upper1[2] - hinge[2] + eps),
            (lower1[0] - hinge[0], lower1[1] - hinge[1], lower1[2] - hinge[2] - eps),
            (lower0[0] - hinge[0], lower0[1] - hinge[1], lower0[2] - hinge[2] - eps),
        ])
    obj = loft(name, rings, exterior, mat, cap_start=True, cap_end=True,
               split_angle_deg=32.0)
    obj.location = hinge


def build_nacelles(exterior, mats):
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        x = side * NACELLE_X
        # Nacelle body, tapering into the wing.
        rings = []
        for y, radius in ((1.30, 0.22), (1.70, 0.45), (2.20, 0.60), (2.90, 0.66),
                          (3.60, 0.62), (4.20, 0.55), (4.70, 0.45), (4.95, 0.32)):
            rings.append([
                (x + radius * math.cos(2.0 * math.pi * i / 24.0), y,
                 0.12 + radius * math.sin(2.0 * math.pi * i / 24.0))
                for i in range(24)
            ])
        loft(f"Nacelle{suffix}", rings, exterior, mats["airframe"],
             cap_start=True, cap_end=True, split_angle_deg=36.0)
        # Radiator under the nacelle.
        box(f"NacelleRadiator{suffix}", (x, 3.10, -0.42), (0.60, 1.10, 0.20),
            exterior, mats["dark"])
        # Spinner and three-blade propeller.
        rings = []
        for y, radius in ((4.72, 0.30), (4.86, 0.27), (4.98, 0.18), (5.08, 0.07),
                          (5.13, 0.010)):
            rings.append([
                (x + radius * math.cos(2.0 * math.pi * i / 24.0), y,
                 0.12 + radius * math.sin(2.0 * math.pi * i / 24.0))
                for i in range(24)
            ])
        loft(f"Spinner{suffix}", rings, exterior, mats["spinner"],
             cap_start=True, cap_end=True, split_angle_deg=45.0)

        prop = empty(f"Propeller{suffix}", (x, PROP_Y, 0.12), exterior)
        all_verts = []
        all_faces = []
        for blade in range(3):
            angle = 2.0 * math.pi * blade / 3.0
            ca, sa = math.cos(angle), math.sin(angle)
            ring_indices = []
            for r in [0.30, 0.55, 0.90, 1.25, 1.50, 1.65]:
                chord = 0.22 + 0.16 * math.sin(math.pi * min(1.0, (r - 0.30) / 1.35))
                pitch = math.radians(44.0 - 24.0 * (r - 0.30) / 1.35)
                section = airfoil_loop(0.04, 0.4, 0.12, count=12)
                start = len(all_verts)
                for cx, cz in section:
                    z_local = (cx - 0.35) * chord
                    y_local = cz * chord
                    y_rot = y_local * math.cos(pitch) - z_local * math.sin(pitch)
                    z_rot = y_local * math.sin(pitch) + z_local * math.cos(pitch)
                    all_verts.append((x + r * ca - z_rot * sa, y_rot, 0.12 + r * sa + z_rot * ca))
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
        blades = mesh_object(f"PropellerBlades{suffix}", all_verts, all_faces, exterior,
                             mats["dark"], smooth=True, split_angle_deg=35.0)
        parent_keep(blades, prop)


def build_tail(exterior, mat):
    # Tailplane.
    rings = []
    for i in range(19):
        t = -1.0 + 2.0 * i / 18.0
        a = abs(t)
        x = 3.0 * t
        chord = 1.70 - (1.70 - 0.55) * a
        le_y = -5.20 - (-5.20 + 5.70) * a
        section = []
        for cx, cz in airfoil_loop(0.0, 0.4, 0.09, count=16):
            section.append((x, le_y - cx * chord, 0.38 + cz * chord))
        rings.append(section)
    loft("Tailplane", rings, exterior, mat, cap_start=True, cap_end=True,
         split_angle_deg=32.0)

    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        t0, t1 = side * 0.08, side * 0.94
        t_mid = 0.5 * (t0 + t1)
        a = abs(t_mid)
        chord_mid = 1.70 - (1.70 - 0.55) * a
        le_mid = -5.20 - (-5.20 + 5.70) * a
        hinge = (3.0 * t_mid, le_mid - 0.74 * chord_mid, 0.38)
        rings = []
        for i in range(7):
            t = t0 + (t1 - t0) * i / 6.0
            aa = abs(t)
            chord = 1.70 - (1.70 - 0.55) * aa
            le_y = -5.20 - (-5.20 + 5.70) * aa
            x = 3.0 * t
            rings.append([
                (x - hinge[0], le_y - 0.74 * chord - hinge[1], 0.38 + 0.05 - hinge[2]),
                (x - hinge[0], le_y - chord - hinge[1], 0.38 - hinge[2]),
                (x - hinge[0], le_y - chord - hinge[1], 0.38 - hinge[2]),
                (x - hinge[0], le_y - 0.74 * chord - hinge[1], 0.38 - 0.05 - hinge[2]),
            ])
        obj = loft(f"Elevator{suffix}", rings, exterior, mat, cap_start=True, cap_end=True,
                   split_angle_deg=32.0)
        obj.location = hinge

    # Fin.
    stations = [
        (0.35, -5.20, 1.75, 0.09),
        (0.65, -5.30, 1.68, 0.09),
        (0.95, -5.45, 1.55, 0.085),
        (1.25, -5.65, 1.35, 0.08),
        (1.55, -5.85, 1.10, 0.075),
        (1.80, -6.05, 0.85, 0.07),
        (2.00, -6.22, 0.65, 0.06),
        (2.12, -6.36, 0.48, 0.05),
        (2.18, -6.48, 0.34, 0.045),
    ]
    rings = []
    for z, le_y, chord, thickness in stations:
        rings.append([
            (cz * chord, le_y - cx * chord, z)
            for cx, cz in airfoil_loop(0.0, 0.4, thickness, count=18)
        ])
    loft("Fin", rings, exterior, mat, cap_start=True, cap_end=True, split_angle_deg=32.0)

    rudder_outline = [
        (-6.50, 0.38), (-6.52, 0.70), (-6.54, 1.00), (-6.56, 1.30),
        (-6.58, 1.55), (-6.68, 1.72), (-6.78, 1.62), (-6.82, 1.35),
        (-6.84, 1.00), (-6.82, 0.65), (-6.78, 0.38),
    ]
    hinge_y, hinge_z = -6.50, 1.00
    half = 0.04
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
    obj.empty_display_size = 0.25
    obj.location = location
    return obj


def parent_keep(child, parent):
    bpy.context.view_layer.update()
    child.parent = parent
    child.matrix_parent_inverse = parent.matrix_world.inverted()


def build_gear(exterior, metal, tyre):
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        pivot = empty(f"GearMain{suffix}", (side * 1.92, 1.17, -0.55), exterior)
        leg = cylinder_between(
            f"GearLeg{suffix}", (side * 1.92, 1.17, -0.55),
            (side * 2.02, 1.17, -1.17), 0.075, exterior, metal,
        )
        wheel = cylinder(
            f"GearWheel{suffix}", (side * 2.04, 1.17, -1.17),
            0.48, 0.30, exterior, tyre, axis=(1.0, 0.0, 0.0), vertices=24,
        )
        hub = cylinder(
            f"GearHub{suffix}", (side * 2.04, 1.17, -1.17),
            0.16, 0.31, exterior, metal, axis=(1.0, 0.0, 0.0), vertices=16,
        )
        for part in (leg, wheel, hub):
            parent_keep(part, pivot)

    tail_pivot = empty("GearTail", (0.0, -5.72, -0.20), exterior)
    tail_leg = cylinder_between("GearTailLeg", (0.0, -5.72, -0.20),
                                (0.0, -5.82, -0.30), 0.055, exterior, metal)
    tail_wheel = cylinder("GearTailWheel", (0.0, -5.82, -0.295),
                          0.20, 0.12, exterior, tyre, axis=(1.0, 0.0, 0.0), vertices=18)
    for part in (tail_leg, tail_wheel):
        parent_keep(part, tail_pivot)


def canopy_ring(y, width, top, base=0.58, count=19, exponent=2.2):
    power = 2.0 / exponent
    ring = []
    for i in range(count):
        theta = math.pi * i / (count - 1)
        c, s = math.cos(theta), math.sin(theta)
        x = width * math.copysign(abs(c) ** power, c)
        z = base + (top - base) * (abs(s) ** power)
        ring.append((x, y, z))
    return ring


def build_greenhouse(exterior, glass, frame):
    stations = [
        (6.85, 0.30, 0.68), (6.60, 0.48, 0.82), (6.20, 0.62, 0.95),
        (5.70, 0.72, 1.05), (5.20, 0.78, 1.12), (4.80, 0.80, 1.12),
    ]
    count = 19
    rings = [canopy_ring(y, width, top, base=0.62, count=count) for y, width, top in stations]
    mesh_object(
        "GreenhouseGlass",
        [v for ring in rings for v in ring],
        [
            (i * count + j, i * count + j + 1,
             (i + 1) * count + j + 1, (i + 1) * count + j)
            for i in range(len(rings) - 1)
            for j in range(count - 1)
        ],
        exterior, glass, smooth=True, split_angle_deg=40.0,
    )
    for index in range(len(stations)):
        y, width, top = stations[index]
        points = [
            (width * math.cos(math.pi * i / 12.0), y,
             0.62 + (top - 0.62) * math.sin(math.pi * i / 12.0))
            for i in range(13)
        ]
        tube_from_points(f"GreenhouseFrame{index}", points, 0.025, exterior, frame,
                         resolution=1)
    for side in (-1.0, 1.0):
        points = [(side * width, y, 0.62) for y, width, _ in stations]
        tube_from_points(f"GreenhouseRail{'L' if side < 0 else 'R'}", points,
                         0.020, exterior, frame, resolution=1)


def build_gondola(exterior, mats):
    stations = [
        (0.60, 0.16, -0.82, -1.12),
        (0.20, 0.30, -0.80, -1.28),
        (-0.50, 0.38, -0.78, -1.36),
        (-1.20, 0.34, -0.76, -1.30),
        (-1.90, 0.22, -0.72, -1.16),
        (-2.30, 0.08, -0.70, -1.02),
    ]
    rings = [superellipse(y, w, top, bottom, count=18, exponent=2.1)
             for y, w, top, bottom in stations]
    loft("BolaGondola", rings, exterior, mats["airframe"], cap_start=True, cap_end=True,
         split_angle_deg=36.0)
    box("BolaGlass", (0.0, 0.56, -0.86), (0.34, 0.02, 0.24), exterior, mats["glass"])


def build_details(exterior, mats):
    dark = mats["dark"]
    metal = mats["metal"]
    # Dive brakes under the outer wings.
    for side in (-1.0, 1.0):
        box(f"DiveBrake{side:+.0f}", (side * 4.4, -0.45, -0.55),
            (1.25, 0.08, 0.38), exterior, dark)
    # Aerial mast and pitot.
    cylinder_between("AntennaMast", (0.0, -2.6, 0.78), (0.0, -2.6, 1.18),
                     0.018, exterior, dark)
    cylinder_between("Pitot", (-8.8, 0.20, -0.30), (-8.8, 1.10, -0.30),
                     0.020, exterior, metal, vertices=8)
    # A few windows / panel details on the nose.
    for side in (-1.0, 1.0):
        box(f"NosePanel{side:+.0f}", (side * 0.86, 5.55, 0.20),
            (0.02, 0.60, 0.30), exterior, dark)


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
    power = 2.0 / 2.25
    c, s = math.cos(angle), math.sin(angle)
    x = width * offset * math.copysign(abs(c) ** power, c)
    z = z_center + z_radius * offset * math.copysign(abs(s) ** power, s)
    return x, y, z


def fuselage_side_x(y, z, side, offset=0.01):
    width, z_top, z_bottom = fuselage_station_at(y)
    z_center = 0.5 * (z_top + z_bottom)
    z_radius = max(1e-6, 0.5 * (z_top - z_bottom))
    t = min(1.0, abs((z - z_center) / z_radius))
    x = width * (max(0.0, 1.0 - t**2.25) ** (1.0 / 2.25))
    return side * (x + offset)


def build_cross_fuselage(name, y, z, side, exterior, mats):
    x = fuselage_side_x(y, z, side)
    box(f"{name}_white_h", (x, y, z), (0.014, 0.78, 0.20), exterior, mats["cross_white"])
    box(f"{name}_white_v", (x, y, z), (0.014, 0.20, 0.78), exterior, mats["cross_white"])
    xb = x + side * 0.005
    box(f"{name}_black_h", (xb, y, z), (0.014, 0.58, 0.11), exterior, mats["cross_black"])
    box(f"{name}_black_v", (xb, y, z), (0.014, 0.11, 0.58), exterior, mats["cross_black"])


def build_cross_wing(name, center_x, center_y, upper, exterior, mats):
    t = center_x / SPAN_HALF
    _, chord, le_y, _ = wing_planform(t)
    c = max(0.0, min(1.0, (le_y - center_y) / max(chord, 1e-6)))
    z = wing_point(t, c, upper)[2] + (0.014 if upper else -0.014)
    box(f"{name}_white_h", (center_x, center_y, z), (0.95, 0.20, 0.012),
        exterior, mats["cross_white"])
    box(f"{name}_white_v", (center_x, center_y, z), (0.20, 0.95, 0.012),
        exterior, mats["cross_white"])
    zb = z + (0.005 if upper else -0.005)
    box(f"{name}_black_h", (center_x, center_y, zb), (0.68, 0.11, 0.012),
        exterior, mats["cross_black"])
    box(f"{name}_black_v", (center_x, center_y, zb), (0.11, 0.68, 0.012),
        exterior, mats["cross_black"])


def build_panel_lines(exterior, mats):
    line = mats["panel_line"]
    for y in (6.60, 6.10, 5.50, 4.90, 4.20, 3.40, 2.50, 1.50, 0.40, -0.70,
              -1.80, -2.90, -4.00, -5.00, -5.90, -6.60):
        points = [fuselage_surface_point(y, 2.0 * math.pi * i / 24.0) for i in range(24)]
        tube_from_points(f"PanelFrame{y:.1f}", points, 0.014, exterior, line, resolution=0)
        rivet_strip(f"RivetsFrame{y:.1f}", points, exterior, line)

    for angle in (0.0, math.pi * 0.5, math.pi, math.pi * 1.5,
                  math.pi * 0.25, math.pi * 0.75, math.pi * 1.25, math.pi * 1.75):
        points = [fuselage_surface_point(6.9 - 0.3 * i, angle) for i in range(45)]
        tube_from_points(f"PanelLongitude{angle:.2f}", points, 0.012, exterior, line,
                         resolution=0)
        rivet_strip(f"RivetsLongitude{angle:.2f}", points, exterior, line)

    for t in (-0.9, -0.7, -0.5, -0.3, -0.1, 0.1, 0.3, 0.5, 0.7, 0.9):
        for upper in (True, False):
            points = [wing_point(t, c / 10.0, upper) for c in range(1, 10)]
            tube_from_points(f"WingRib{t:+.2f}{upper}", points, 0.012, exterior, line,
                             resolution=0)
            rivet_strip(f"RivetsWingRib{t:+.2f}{upper}", points, exterior, line)
    for c in (0.20, 0.50, 0.80):
        for upper in (True, False):
            points = [wing_point(-0.95 + 1.90 * i / 14.0, c, upper) for i in range(15)]
            tube_from_points(f"WingSpan{c:.2f}{upper}", points, 0.012, exterior, line,
                             resolution=0)
            rivet_strip(f"RivetsWingSpan{c:.2f}{upper}", points, exterior, line)

    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        build_cross_fuselage(f"BalkenkreuzFuselage{suffix}", -2.4, 0.10, side,
                             exterior, mats)
        build_cross_wing(f"BalkenkreuzWingTop{suffix}", side * 5.4, 0.50, True,
                         exterior, mats)
        build_cross_wing(f"BalkenkreuzWingBottom{suffix}", side * 5.4, 0.50, False,
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


def consolidate(exterior):
    protected = set()
    roots = ["PropellerL", "PropellerR", "AileronL", "AileronR", "FlapL", "FlapR",
             "ElevatorL", "ElevatorR", "Rudder", "GearMainL", "GearMainR", "GearTail"]
    for name in roots:
        obj = bpy.data.objects.get(name)
        if obj is not None:
            protected.update(id(part) for part in descendants(obj))
    static = [obj for obj in collection_objects(exterior)
              if obj.type == "MESH" and id(obj) not in protected]
    join_meshes("ExteriorStatic", static, exterior)


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    model_dir = os.path.join(root, "game", "assets", "aircraft", "ju88-a4", "model")
    source_dir = os.path.join(root, "art", "aircraft", "ju88-a4")
    os.makedirs(model_dir, exist_ok=True)
    os.makedirs(source_dir, exist_ok=True)

    reset_scene()
    exterior = new_collection("EXTERIOR")

    texture_dir = os.path.join(model_dir, "textures")
    mats = {
        "airframe": image_material(
            "airframe", os.path.join(texture_dir, "ju88_camo_albedo.png"),
            os.path.join(texture_dir, "ju88_camo_normal.png"),
            roughness=0.60, metallic=0.04, uv_scale=1.0,
            orm_path=os.path.join(texture_dir, "ju88_camo_orm.png"),
        ),
        "underside": material("underside", (0.46, 0.58, 0.66), 0.62, 0.03),
        "metal": material("metal", (0.42, 0.44, 0.43), 0.34, 0.70),
        "dark": material("dark", (0.035, 0.040, 0.035), 0.84, 0.08),
        "tyre": material("tyre", (0.025, 0.025, 0.025), 0.95, 0.0),
        "glass": material("glass", (0.24, 0.30, 0.32), 0.06, 0.25, alpha=0.50,
                          double_sided=True),
        "interior": material("interior", (0.075, 0.085, 0.075), 0.86, 0.02),
        "panel_line": material("panel_line", (0.030, 0.035, 0.030), 0.90, 0.02),
        "cross_white": material("cross_white", (0.82, 0.82, 0.78), 0.62, 0.0,
                                double_sided=True),
        "cross_black": material("cross_black", (0.015, 0.015, 0.015), 0.70, 0.0,
                                double_sided=True),
        "spinner": material("spinner", (0.10, 0.12, 0.085), 0.55, 0.06),
    }

    build_fuselage(exterior, mats["airframe"])
    build_wing(exterior, mats["airframe"])
    build_wing_panel("AileronL", -0.94, -0.56, exterior, mats["airframe"])
    build_wing_panel("AileronR", 0.56, 0.94, exterior, mats["airframe"])
    build_wing_panel("FlapL", -0.55, -0.10, exterior, mats["airframe"])
    build_wing_panel("FlapR", 0.10, 0.55, exterior, mats["airframe"])
    build_tail(exterior, mats["airframe"])
    build_nacelles(exterior, mats)
    build_gear(exterior, mats["metal"], mats["tyre"])
    build_greenhouse(exterior, mats["glass"], mats["dark"])
    build_gondola(exterior, mats)
    build_details(exterior, mats)
    build_panel_lines(exterior, mats)
    assign_underside(exterior, mats["underside"])
    empty("DamageSmokeAnchor", (0.0, 2.6, 0.4), exterior)

    consolidate(exterior)
    meshes = collection_objects(exterior)
    uv_unwrap_all(meshes)

    bake_detail_normal(
        bpy.data.objects.get("ExteriorStatic"),
        os.path.join(texture_dir, "ju88_camo_normal.png"),
        size=4096,
        ignore_materials=("cross_white", "cross_black"),
    )
    ao_path = os.path.join(texture_dir, "ju88_camo_ao.png")
    bake_ao(bpy.data.objects.get("ExteriorStatic"), ao_path, size=4096)
    attach_occlusion(mats["airframe"], ao_path)

    scene = bpy.context.scene
    scene["aircraft_id"] = "ju88-a4"
    scene["display_name"] = "Junkers Ju 88 A-4"
    scene["frame"] = "Blender +Y nose, +Z up, metres, origin at CoG"
    scene["model_variant"] = "A-4, Jumo 211, greenhouse nose, Bola gondola"

    bpy.context.preferences.filepaths.save_version = 0
    blend_path = os.path.join(source_dir, "ju88-a4.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend_path)
    glb_path = os.path.join(model_dir, "ju88-a4_lod0.glb")
    export_glb(glb_path, meshes)
    print(f"Wrote {glb_path}")
    print(f"Wrote {blend_path}")


if __name__ == "__main__":
    main()
