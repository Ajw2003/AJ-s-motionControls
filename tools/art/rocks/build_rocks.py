"""Builds the six shore-rock meshes from rock_shape.py and exports them for Unity.

Run either way (needs bpy, not importable from system Python on this machine):
    "C:\\Program Files\\Blender Foundation\\Blender 4.5\\blender.exe" --background --python
        tools/art/rocks/build_rocks.py [-- --no-export]

Reads   tools/art/rocks/rocks.json (via rock_shape.load_rocks)
Writes  Assets/Resources/Fbx/Rocks.fbx   (skipped with --no-export)

Coordinates. rock_shape.py works in Unity's frame: x right (long axis), y up, z forward.
Blender's frame here follows the Lake Louise precedent (build_lake_louise.py): X = Unity x,
Y = Unity z, Z = Unity y. So a rock_shape vertex (x, y, z) maps to Blender (x, z, y) — swap the
last two axes.

That swap is a reflection (it flips handedness), so it reverses every face's winding relative
to what rock_shape.py computed as "outward" in Unity space. Each face's vertex index order is
therefore reversed when it is mapped in, and bmesh.ops.recalc_face_normals is run afterwards as
a second, independent check — the build asserts every resulting face normal points away from
the mesh centre (dot product with the centroid-to-face-centre vector > 0) and exits non-zero if
any does not, so a winding mistake cannot pass silently.

Object name AND mesh datablock name are set to "Rock_0N_<Name>" (e.g. "Rock_01_Skimmer") so
that RockCatalogBuilder's ordinal name sort ("Rock_01_..." < "Rock_02_..." < ...) lines the
meshes up in shore order without relying on FBX sub-asset export order.

Objects are built and exported at the origin, identity transform (no object-level translate,
rotate or scale) — world-size metres, origin at each mesh's own bounding-box centre (rock_shape
already recentres). preview.py is the one that lays them out spaced along X, only for its own
render; that layout never touches the exported data here.

Meshes are triangulated and flat-shaded (use_smooth = False) — D2's flat-shaded low-poly style,
matching make_sheets.py's Lambert-shaded sheets. No materials beyond Blender's implicit default
slot; no vertex colours (the player's colour tints the rock at runtime, not the mesh).

Per rock this prints the triangle count and the Unity-frame bounding-box dimensions in
centimetres, read back from the built Blender mesh and mapped back to Unity axes (undoing the
X,Z,Y swap). Those are compared against rock_shape's own noise-free dimensions for the same
params (within 0.1 cm) and the thickness/length ratio is checked against < 0.25 — the same
realism rule rock_shape.py --check enforces. Any mismatch exits non-zero.

Export settings are copied verbatim from build_lake_louise.py's export_fbx (see that function's
docstring for why -Z forward / Y up / bake_space_transform is the setting that lands right way
up in Unity — it was observed, not assumed, on this same pipeline).
"""

import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
FBX_PATH = REPO / "Assets" / "Resources" / "Fbx" / "Rocks.fbx"

sys.path.insert(0, str(HERE))

import bpy  # noqa: E402  (after sys.path insert, same order as build_lake_louise.py)
import bmesh  # noqa: E402

import rock_shape  # noqa: E402

DIM_TOLERANCE_M = 0.001  # 0.1 cm
THICKNESS_RATIO_MAX = 0.25


def unity_to_blender(vertex):
    """(x, y, z) Unity -> Blender (x, z, y). A reflection: callers must reverse face winding."""
    x, y, z = vertex
    return (x, z, y)


def blender_dims_to_unity_cm(dims_blender):
    """Blender (X, Y, Z) bbox size -> Unity (x, y, z) bbox size, in centimetres."""
    bx, by, bz = dims_blender
    return (bx * 100.0, bz * 100.0, by * 100.0)


def object_name_for(params, index):
    return f"Rock_{index + 1:02d}_{params['name']}"


def build_object(params, index):
    """One rock_shape mesh -> one Blender object, named and shaded per the module docstring."""
    vertices, faces = rock_shape.build(params)

    blender_vertices = [unity_to_blender(v) for v in vertices]
    # The axis swap is a reflection (det -1), so faces computed as outward-wound in Unity
    # space wind inward once mapped — reverse each face's index order to compensate.
    blender_faces = [list(reversed(f)) for f in faces]

    name = object_name_for(params, index)
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(blender_vertices, [], blender_faces)
    mesh.update()

    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def triangulate(obj):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(obj.data)
    bm.free()


def recalc_and_verify_normals(obj):
    """bmesh.ops.recalc_face_normals, then assert every face points away from mesh centre.

    A second, independent check on top of the winding-reversal in build_object — recalc can
    itself get a mesh's global orientation backwards on a topology it cannot disambiguate, so
    this asserts the actual outcome rather than trusting the fix that produced it.

    Run BEFORE triangulate, on the same quad/triangle-fan faces rock_shape.py's own --check
    verifies (its centroid-vs-normal test, which already passes 0 bad on every rock). Checking
    afterward instead would test individual post-triangulation half-quads, where a jittered,
    non-planar quad's own two triangles can legitimately fail a whole-mesh centroid heuristic
    even though the quad they came from is correctly wound — rock_shape.py's docstring notes
    exactly this ("jittered quads are not perfectly planar"). Triangulating a correctly-wound
    face preserves its winding, so checking before is equivalent for catching a real mistake
    (an axis-mapping or reversal bug) without tripping on that geometric non-planarity.
    """
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()

    verts = obj.data.vertices
    cx = sum(v.co.x for v in verts) / len(verts)
    cy = sum(v.co.y for v in verts) / len(verts)
    cz = sum(v.co.z for v in verts) / len(verts)

    bad = 0
    for poly in obj.data.polygons:
        fx, fy, fz = poly.center
        to_face = (fx - cx, fy - cy, fz - cz)
        nx, ny, nz = poly.normal
        dot = to_face[0] * nx + to_face[1] * ny + to_face[2] * nz
        if dot <= 0.0:
            bad += 1
    if bad:
        print(f"NORMAL CHECK FAILED for {obj.name}: {bad} face(s) point inward.", file=sys.stderr)
        return False
    return True


def flat_shade(objects):
    for obj in objects:
        for polygon in obj.data.polygons:
            polygon.use_smooth = False


def check_dims(params, obj):
    """Compare the built Blender mesh's Unity-frame bbox (cm), read back and mapped through
    unity_to_blender's inverse, against rock_shape.build()'s own dims for the SAME params
    (same jitter, same seed) — this is a round-trip check on the axis mapping and winding
    reversal, not a re-derivation of the shape: rock_shape.build() is the ground truth for
    what the mesh should measure, so it should match to within floating-point noise, not to
    jitter's amplitude."""
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    dims_blender = (max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs))
    ux_cm, uy_cm, uz_cm = blender_dims_to_unity_cm(dims_blender)

    expected_vertices, _ = rock_shape.build(params)
    ex_xs = [v[0] for v in expected_vertices]
    ex_ys = [v[1] for v in expected_vertices]
    ex_zs = [v[2] for v in expected_vertices]
    expected_x_cm = (max(ex_xs) - min(ex_xs)) * 100.0
    expected_y_cm = (max(ex_ys) - min(ex_ys)) * 100.0
    expected_z_cm = (max(ex_zs) - min(ex_zs)) * 100.0

    tol_cm = DIM_TOLERANCE_M * 100.0
    dims_ok = (
        abs(ux_cm - expected_x_cm) <= tol_cm
        and abs(uy_cm - expected_y_cm) <= tol_cm
        and abs(uz_cm - expected_z_cm) <= tol_cm
    )

    ratio = (uy_cm / 100.0) / max(ux_cm / 100.0, uz_cm / 100.0)
    ratio_ok = ratio < THICKNESS_RATIO_MAX

    tris = len(obj.data.polygons)
    print(
        f"{params['name']:<8} tris={tris:3d}  "
        f"unity_dims_cm=({ux_cm:5.1f}, {uy_cm:5.1f}, {uz_cm:5.1f})  "
        f"expected_cm=({expected_x_cm:5.1f}, {expected_y_cm:5.1f}, {expected_z_cm:5.1f})  "
        f"dims_ok={dims_ok}  thickness_ratio={ratio:.3f} ratio_ok={ratio_ok}"
    )
    return dims_ok and ratio_ok


def export_fbx(objects):
    """Same settings as build_lake_louise.export_fbx — see that function's docstring for why
    -Z forward / Y up / bake_space_transform is the mapping that lands right way up in Unity
    on this pipeline (observed on the Lake Louise export, not assumed)."""
    FBX_PATH.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=str(FBX_PATH),
        use_selection=True,
        object_types={"MESH"},
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=True,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        mesh_smooth_type="FACE",
        colors_type="SRGB",
        use_mesh_modifiers=True,
        add_leaf_bones=False,
        bake_anim=False,
    )
    print(f"\nExported {FBX_PATH.relative_to(REPO)} ({FBX_PATH.stat().st_size} bytes)")


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def build():
    """Builds all six rocks into the current (emptied) scene. Returns the object list.

    recalc_and_verify_normals runs before triangulate — see that function's docstring — and
    check_dims (bbox size) is unaffected by triangulation either way, so it runs after purely
    to report against the final, exported topology's triangle count.
    """
    clear_scene()
    objects = []
    ok = True
    for index, params in enumerate(rock_shape.load_rocks()):
        obj = build_object(params, index)
        if not recalc_and_verify_normals(obj):
            ok = False
        triangulate(obj)
        if not check_dims(params, obj):
            ok = False
        objects.append(obj)
    flat_shade(objects)
    return objects, ok


def main():
    objects, ok = build()

    if not ok:
        print("\nBUILD FAILED — see NORMAL CHECK / dims_ok / ratio_ok above.", file=sys.stderr)
        return 1

    if "--no-export" not in sys.argv:
        export_fbx(objects)
    else:
        print("\n--no-export: skipped FBX export.")

    return 0


if __name__ == "__main__":
    code = main()
    if code:
        sys.exit(code)
