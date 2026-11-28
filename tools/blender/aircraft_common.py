"""Shared Blender helpers for Iron Skies aircraft assets.

Conventions
-----------
* Metres, scale 1.0.
* Nose along +Y, up +Z, right +X.
* Origin at the centre of gravity (the simulation body origin).
* The glTF exporter converts this to Godot's +Y up / -Z forward frame.

Run inside Blender only, e.g.:

    blender --background --python tools/blender/build_spitfire.py
"""

from __future__ import annotations

import math
import os
from typing import Iterable, Sequence

import bmesh
import bpy
from mathutils import Matrix, Vector


# ---------------------------------------------------------------------------
# Scene / collections
# ---------------------------------------------------------------------------


def reset_scene() -> None:
    """Start from an empty scene with metric units."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene.render.engine = "BLENDER_EEVEE_NEXT"


def new_collection(name: str, parent: bpy.types.Collection | None = None) -> bpy.types.Collection:
    collection = bpy.data.collections.new(name)
    (parent or bpy.context.scene.collection).children.link(collection)
    return collection


def link_object(obj: bpy.types.Object, collection: bpy.types.Collection) -> bpy.types.Object:
    for existing in list(obj.users_collection):
        existing.objects.unlink(obj)
    collection.objects.link(obj)
    return obj


# ---------------------------------------------------------------------------
# Materials
# ---------------------------------------------------------------------------


def material(
    name: str,
    color: Sequence[float],
    roughness: float = 0.6,
    metallic: float = 0.0,
    alpha: float = 1.0,
    double_sided: bool = False,
) -> bpy.types.Material:
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    mat.use_backface_culling = not double_sided
    if hasattr(mat, "blend_method"):
        mat.blend_method = "BLEND" if alpha < 1.0 else "OPAQUE"
    if hasattr(mat, "surface_render_method"):
        mat.surface_render_method = "BLENDED" if alpha < 1.0 else "DITHERED"
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    if alpha < 1.0:
        bsdf.inputs["Alpha"].default_value = alpha
        bsdf.inputs["IOR"].default_value = 1.45
    return mat


def image_material(
    name: str,
    albedo_path: str,
    normal_path: str | None = None,
    roughness: float = 0.55,
    metallic: float = 0.05,
    uv_scale: float = 1.0,
) -> bpy.types.Material:
    """PBR material using a packed albedo image and an optional normal map."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    links = mat.node_tree.links
    bsdf = nodes.get("Principled BSDF")
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic

    geometry = nodes.new("ShaderNodeNewGeometry")
    mapping = nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (uv_scale, uv_scale, uv_scale)
    links.new(geometry.outputs["Position"], mapping.inputs["Vector"])

    albedo = bpy.data.images.load(albedo_path, check_existing=True)
    albedo.colorspace_settings.name = "sRGB"
    albedo.pack()
    albedo_node = nodes.new("ShaderNodeTexImage")
    albedo_node.image = albedo
    albedo_node.interpolation = "Linear"
    albedo_node.projection = "BOX"
    albedo_node.projection_blend = 0.35
    links.new(mapping.outputs["Vector"], albedo_node.inputs["Vector"])
    links.new(albedo_node.outputs["Color"], bsdf.inputs["Base Color"])

    if normal_path and os.path.exists(normal_path):
        normal_image = bpy.data.images.load(normal_path, check_existing=True)
        normal_image.colorspace_settings.name = "Non-Color"
        normal_image.pack()
        normal_node = nodes.new("ShaderNodeTexImage")
        normal_node.image = normal_image
        normal_node.interpolation = "Linear"
        # The bake is in UV space; do not reuse the triplanar albedo mapping.
        normal_map = nodes.new("ShaderNodeNormalMap")
        links.new(normal_node.outputs["Color"], normal_map.inputs["Color"])
        links.new(normal_map.outputs["Normal"], bsdf.inputs["Normal"])

    return mat


# ---------------------------------------------------------------------------
# Mesh construction
# ---------------------------------------------------------------------------


def mesh_object(
    name: str,
    verts: Iterable[Sequence[float]],
    faces: Iterable[Sequence[int]],
    collection: bpy.types.Collection,
    mat: bpy.types.Material | None = None,
    smooth: bool = True,
    split_angle_deg: float = 40.0,
) -> bpy.types.Object:
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([tuple(v) for v in verts], [], [tuple(f) for f in faces])
    mesh.update()

    # Make normals consistent; lofted surfaces often start inside-out.
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(mesh)
    bm.free()

    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    if mat is not None:
        mesh.materials.append(mat)

    if smooth:
        for polygon in mesh.polygons:
            polygon.use_smooth = True
        modifier = obj.modifiers.new("EdgeSplit", "EDGE_SPLIT")
        modifier.split_angle = math.radians(split_angle_deg)
        modifier.use_edge_angle = True
        modifier.use_edge_sharp = True
    return obj


def loft(
    name: str,
    rings: Sequence[Sequence[Sequence[float]]],
    collection: bpy.types.Collection,
    mat: bpy.types.Material | None = None,
    cap_start: bool = True,
    cap_end: bool = True,
    smooth: bool = True,
    split_angle_deg: float = 40.0,
) -> bpy.types.Object:
    """Loft equal-length rings into a closed tube (quads + optional end caps)."""
    if not rings:
        raise ValueError(f"{name}: no rings")
    count = len(rings[0])
    if any(len(ring) != count for ring in rings):
        raise ValueError(f"{name}: rings differ in length")

    verts: list[Sequence[float]] = []
    for ring in rings:
        verts.extend(ring)

    faces: list[Sequence[int]] = []
    for i in range(len(rings) - 1):
        for j in range(count):
            a = i * count + j
            b = i * count + (j + 1) % count
            c = (i + 1) * count + (j + 1) % count
            d = (i + 1) * count + j
            faces.append((a, b, c, d))
    if cap_start:
        faces.append(tuple(range(count - 1, -1, -1)))
    if cap_end:
        start = (len(rings) - 1) * count
        faces.append(tuple(range(start, start + count)))
    return mesh_object(name, verts, faces, collection, mat, smooth, split_angle_deg)


# ---------------------------------------------------------------------------
# Sections
# ---------------------------------------------------------------------------


def superellipse(
    y: float,
    half_width: float,
    z_top: float,
    z_bottom: float,
    count: int = 28,
    exponent: float = 2.3,
) -> list[tuple[float, float, float]]:
    """Closed rounded-rectangle section in the XZ plane at station *y*."""
    z_center = 0.5 * (z_top + z_bottom)
    z_radius = 0.5 * (z_top - z_bottom)
    power = 2.0 / exponent
    points = []
    for i in range(count):
        angle = 2.0 * math.pi * i / count
        c = math.cos(angle)
        s = math.sin(angle)
        x = half_width * math.copysign(abs(c) ** power, c)
        z = z_center + z_radius * math.copysign(abs(s) ** power, s)
        points.append((x, y, z))
    return points


def naca_offset(m: float, p: float, t: float, x: float) -> tuple[float, float]:
    """NACA 4-digit mean camber and half-thickness at chord fraction *x*."""
    x = min(max(x, 0.0), 1.0)
    yt = 5.0 * t * (
        0.2969 * math.sqrt(x)
        - 0.1260 * x
        - 0.3516 * x * x
        + 0.2843 * x**3
        - 0.1015 * x**4
    )
    if m <= 0.0 or p <= 0.0 or p >= 1.0:
        return 0.0, yt, 0.0
    if x < p:
        yc = m / (p * p) * (2.0 * p * x - x * x)
        dyc = 2.0 * m / (p * p) * (p - x)
    else:
        yc = m / ((1.0 - p) ** 2) * ((1.0 - 2.0 * p) + 2.0 * p * x - x * x)
        dyc = 2.0 * m / ((1.0 - p) ** 2) * (p - x)
    theta = math.atan(dyc)
    return yc, yt, theta


def naca_section(
    m: float,
    p: float,
    t: float,
    count: int = 24,
) -> list[tuple[float, float]]:
    """Closed NACA section: upper LE->TE then lower TE->LE, chord fraction 0..1."""
    xs = [0.5 * (1.0 - math.cos(math.pi * i / (count - 1))) for i in range(count)]
    upper: list[tuple[float, float]] = []
    lower: list[tuple[float, float]] = []
    for x in xs:
        yc, yt, theta = naca_offset(m, p, t, x)
        xu = x - yt * math.sin(theta)
        yu = yc + yt * math.cos(theta)
        xl = x + yt * math.sin(theta)
        yl = yc - yt * math.cos(theta)
        upper.append((xu, yu))
        lower.append((xl, yl))
    section = upper + list(reversed(lower[1:-1]))
    return section


def airfoil_loop(
    m: float,
    p: float,
    t: float,
    count: int = 24,
) -> list[tuple[float, float]]:
    """Same as :func:`naca_section` but with unique LE/TE points."""
    xs = [0.5 * (1.0 - math.cos(math.pi * i / (count - 1))) for i in range(count)]
    upper: list[tuple[float, float]] = []
    lower: list[tuple[float, float]] = []
    for x in xs:
        yc, yt, theta = naca_offset(m, p, t, x)
        upper.append((x - yt * math.sin(theta), yc + yt * math.cos(theta)))
        lower.append((x + yt * math.sin(theta), yc - yt * math.cos(theta)))
    return upper + list(reversed(lower[1:-1]))


# ---------------------------------------------------------------------------
# Primitives
# ---------------------------------------------------------------------------


def box(
    name: str,
    center: Sequence[float],
    size: Sequence[float],
    collection: bpy.types.Collection,
    mat: bpy.types.Material | None = None,
    rotation: Sequence[float] = (0.0, 0.0, 0.0),
) -> bpy.types.Object:
    sx, sy, sz = (0.5 * s for s in size)
    verts = [
        (-sx, -sy, -sz), (sx, -sy, -sz), (sx, sy, -sz), (-sx, sy, -sz),
        (-sx, -sy, sz), (sx, -sy, sz), (sx, sy, sz), (-sx, sy, sz),
    ]
    faces = [
        (0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
        (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7),
    ]
    obj = mesh_object(name, verts, faces, collection, mat, smooth=False)
    obj.location = center
    obj.rotation_euler = rotation
    return obj


def cylinder(
    name: str,
    center: Sequence[float],
    radius: float,
    depth: float,
    collection: bpy.types.Collection,
    mat: bpy.types.Material | None = None,
    axis: Sequence[float] = (0.0, 0.0, 1.0),
    vertices: int = 20,
    smooth: bool = True,
) -> bpy.types.Object:
    half = 0.5 * depth
    rings = []
    for z in (-half, half):
        rings.append([
            (radius * math.cos(2.0 * math.pi * i / vertices),
             radius * math.sin(2.0 * math.pi * i / vertices),
             z)
            for i in range(vertices)
        ])
    obj = loft(name, rings, collection, mat, cap_start=True, cap_end=True, smooth=smooth)
    direction = Vector(axis).normalized()
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = direction.to_track_quat("Z", "Y")
    obj.location = center
    return obj


def cylinder_between(
    name: str,
    p1: Sequence[float],
    p2: Sequence[float],
    radius: float,
    collection: bpy.types.Collection,
    mat: bpy.types.Material | None = None,
    vertices: int = 16,
) -> bpy.types.Object:
    a = Vector(p1)
    b = Vector(p2)
    direction = b - a
    obj = cylinder(name, 0.5 * (a + b), radius, direction.length, collection, mat,
                   vertices=vertices)
    return obj


def tube_from_points(
    name: str,
    points: Sequence[Sequence[float]],
    radius: float,
    collection: bpy.types.Collection,
    mat: bpy.types.Material | None = None,
    resolution: int = 2,
) -> bpy.types.Object:
    curve = bpy.data.curves.new(name, "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = radius
    curve.bevel_resolution = resolution
    spline = curve.splines.new("POLY")
    spline.points.add(len(points) - 1)
    for i, point in enumerate(points):
        spline.points[i].co = (*point, 1.0)
    obj = bpy.data.objects.new(name, curve)
    collection.objects.link(obj)
    if mat is not None:
        curve.materials.append(mat)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.convert(target="MESH")
    obj.select_set(False)
    return obj


def extrude_outline(
    name: str,
    outline_yz: Sequence[Sequence[float]],
    thickness_x: float,
    collection: bpy.types.Collection,
    mat: bpy.types.Material | None = None,
) -> bpy.types.Object:
    """Extrude a closed (y, z) outline symmetrically along X."""
    half = 0.5 * thickness_x
    n = len(outline_yz)
    verts = [(-half, y, z) for y, z in outline_yz] + [(half, y, z) for y, z in outline_yz]
    faces: list[Sequence[int]] = []
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, n + j, n + i))
    faces.append(tuple(range(n - 1, -1, -1)))
    faces.append(tuple(range(n, 2 * n)))
    return mesh_object(name, verts, faces, collection, mat, smooth=False)


# ---------------------------------------------------------------------------
# Export / metadata
# ---------------------------------------------------------------------------


def collection_objects(collection: bpy.types.Collection) -> list[bpy.types.Object]:
    objects = list(collection.objects)
    for child in collection.children:
        objects.extend(collection_objects(child))
    return objects


def select_only(objects: Sequence[bpy.types.Object]) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.hide_set(False)
        obj.select_set(True)
    if objects:
        bpy.context.view_layer.objects.active = objects[0]


def descendants(obj: bpy.types.Object) -> list[bpy.types.Object]:
    result = [obj]
    for child in obj.children:
        result.extend(descendants(child))
    return result


def join_meshes(
    name: str,
    objects: Sequence[bpy.types.Object],
    collection: bpy.types.Collection,
) -> bpy.types.Object | None:
    """Join mesh objects into one object, preserving the active object's parent."""
    meshes = [obj for obj in objects if obj.type == "MESH"]
    if not meshes:
        return None
    bpy.ops.object.select_all(action="DESELECT")
    for obj in meshes:
        obj.hide_set(False)
        obj.select_set(True)
    active = meshes[0]
    bpy.context.view_layer.objects.active = active
    bpy.ops.object.join()
    active.name = name
    link_object(active, collection)
    if not any(mod.type == "EDGE_SPLIT" for mod in active.modifiers):
        modifier = active.modifiers.new("EdgeSplit", "EDGE_SPLIT")
        modifier.split_angle = math.radians(40.0)
        modifier.use_edge_angle = True
        modifier.use_edge_sharp = True
    return active


def uv_unwrap_all(objects: Sequence[bpy.types.Object], margin: float = 0.015) -> None:
    """Smart-project UVs on every mesh object (blockout atlas)."""
    for obj in objects:
        if obj.type != "MESH":
            continue
        bpy.ops.object.select_all(action="DESELECT")
        obj.hide_set(False)
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.smart_project(
            angle_limit=math.radians(66.0),
            island_margin=margin,
            area_weight=0.0,
            correct_aspect=True,
            scale_to_bounds=False,
        )
        # Smart Project leaves the islands at their natural size; without a
        # pack the atlas was only ~6% covered, wasting most of the 4K detail
        # and camo budget. Pack needs the UVs selected explicitly.
        bpy.ops.uv.select_all(action="SELECT")
        bpy.ops.uv.pack_islands(
            rotate=True, scale=True, margin=0.004, margin_method="SCALED")
        bpy.ops.object.mode_set(mode="OBJECT")
        obj.select_set(False)


def bake_detail_normal(
    obj: bpy.types.Object | None,
    output_path: str,
    size: int = 4096,
    detail_material: str = "panel_line",
    invert: bool = True,
    ignore_materials: Sequence[str] = (
        "roundel_blue", "roundel_white", "roundel_red"),
) -> str | None:
    """Bake the detail geometry into a tangent-space normal map and remove it.

    ``obj`` is the joined low-poly mesh. A duplicate keeps the faces that use
    ``detail_material`` (the panel-line tubes), a second duplicate drops them,
    and Cycles bakes the high copy's normals onto the low copy's existing UVs.
    The result stays aligned to the shipped mesh because both copies share its
    UV map. ``invert`` flips the red/green channels so raised tubes read as
    shallow grooves instead of ridges.

    The detail faces are deleted from ``obj`` afterwards, so the runtime pays
    for the texture instead of the panel-line geometry. Returns the saved path.
    """
    if obj is None or obj.type != "MESH":
        return None
    detail_indices = {
        index for index, material in enumerate(obj.data.materials)
        if material is not None and material.name == detail_material
    }
    if not detail_indices:
        print(f"bake: {obj.name} has no {detail_material!r} faces")
        return None

    collection = obj.users_collection[0]
    high = obj.copy()
    high.data = obj.data.copy()
    high.name = f"{obj.name}BakeHigh"
    collection.objects.link(high)
    low = obj.copy()
    low.data = obj.data.copy()
    low.name = f"{obj.name}BakeLow"
    collection.objects.link(low)

    def drop_faces(mesh: bpy.types.Mesh, indices: set[int]) -> None:
        bm = bmesh.new()
        bm.from_mesh(mesh)
        faces = [face for face in bm.faces if face.material_index in indices]
        if faces:
            bmesh.ops.delete(bm, geom=faces, context="FACES")
        bm.to_mesh(mesh)
        bm.free()

    def drop_detail(mesh: bpy.types.Mesh) -> None:
        drop_faces(mesh, detail_indices)

    # Coplanar decal discs would otherwise bake as raised rings.
    ignore_indices = {
        index for index, material in enumerate(obj.data.materials)
        if material is not None and material.name in ignore_materials
    }
    if ignore_indices:
        drop_faces(high.data, ignore_indices)

    # Sink the base surface just under the low-poly so a ray cast from the low
    # surface reaches the raised detail tubes first instead of the coincident
    # airframe (which would bake a flat map everywhere).
    bm = bmesh.new()
    bm.from_mesh(high.data)
    bm.normal_update()
    base_vertices = {
        vertex for face in bm.faces
        if face.material_index not in detail_indices
        for vertex in face.verts
    }
    for vertex in base_vertices:
        vertex.co -= vertex.normal * 0.008
    bm.to_mesh(high.data)
    bm.free()

    drop_detail(low.data)

    # The bake writes to the active image node of the active object's material.
    # Give the low copy one material so every face shares that node.
    material = low.data.materials[0] if low.data.materials else None
    if material is None or not material.use_nodes:
        raise RuntimeError(f"{obj.name}: no node material to bake into")
    low.data.materials.clear()
    low.data.materials.append(material)
    for polygon in low.data.polygons:
        polygon.material_index = 0

    image = bpy.data.images.new(
        f"{obj.name}_detail_normal", size, size, alpha=False, is_data=True)
    image.colorspace_settings.name = "Non-Color"
    nodes = material.node_tree.nodes
    bake_node = nodes.new("ShaderNodeTexImage")
    bake_node.image = image
    nodes.active = bake_node

    bpy.ops.object.select_all(action="DESELECT")
    high.select_set(True)
    low.select_set(True)
    bpy.context.view_layer.objects.active = low

    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 1
    bpy.ops.object.bake(
        type="NORMAL",
        normal_space="TANGENT",
        use_selected_to_active=True,
        cage_extrusion=0.08,
        max_ray_distance=0.2,
        use_clear=True,
    )

    if invert:
        import numpy as np

        pixels = np.empty(size * size * 4, dtype=np.float32)
        image.pixels.foreach_get(pixels)
        channels = pixels.reshape(-1, 4)
        channels[:, 0] = 1.0 - channels[:, 0]
        channels[:, 1] = 1.0 - channels[:, 1]
        image.pixels.foreach_set(pixels)
        image.update()

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    image.filepath_raw = output_path
    image.file_format = "PNG"
    image.save()

    # Point the material's Normal Map node at the bake and pack it for export.
    normal_source = None
    for node in nodes:
        if node.type != "NORMAL_MAP":
            continue
        for link in node.inputs["Color"].links:
            if link.from_node.type == "TEX_IMAGE":
                normal_source = link.from_node
    if normal_source is not None:
        normal_source.image = image
        image.pack()
    nodes.remove(bake_node)
    bpy.data.objects.remove(high, do_unlink=True)
    bpy.data.objects.remove(low, do_unlink=True)
    drop_detail(obj.data)
    print(f"bake: {output_path} {size}² from {len(detail_indices)} detail material(s)")
    return output_path


def scale_uvs(objects: Sequence[bpy.types.Object], factor: float) -> None:
    for obj in objects:
        if obj.type != "MESH" or not obj.data.uv_layers:
            continue
        layer = obj.data.uv_layers.active
        for loop in layer.data:
            loop.uv *= factor


def export_glb(path: str, objects: Sequence[bpy.types.Object]) -> None:
    select_only(objects)
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        export_yup=True,
        export_materials="EXPORT",
        export_texcoords=True,
        export_normals=True,
        export_tangents=False,
        export_animations=False,
        export_cameras=False,
        export_lights=False,
        export_extras=True,
    )
