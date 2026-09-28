"""Renders the six shore rocks side by side, for checking against the 2D reference sheets.

Run:
    "C:\\Program Files\\Blender Foundation\\Blender 4.5\\blender.exe" --background --python
        tools/art/rocks/preview.py

Reads   tools/art/rocks/rocks.json (via build_rocks.build(), --no-export path — nothing here
        touches Assets/Resources/Fbx/Rocks.fbx)
Writes  tools/art/rocks/preview.png   (~1600x500)

Builds the six rocks with build_rocks.build() (no export), lays them out left to right with
gaps along Blender X (= Unity x, the same axis build_rocks keeps identity on for export — this
layout is preview-only and never touches the exported data), and renders one flat-lit 3/4 view,
roughly matching make_sheets.py's 3/4 silhouette view: yaw ~35 degrees around the vertical axis,
pitched ~25 degrees down, orthographic.

Engine: this machine's headless Blender has no GPU context, so Workbench (a rasterizer,
same requirement as EEVEE) is not reliably available in --background — the Lake Louise preview
(preview.py in that folder) hit exactly this and uses Cycles on the CPU with an emission-only
material instead, which needs no GPU and still reads as flat/unlit. This script follows that
precedent: one grey emission material, a single Lambert-style shade term from face normals
(matching make_sheets.py's flat-shaded look), no denoising needed since the material has no
noisy indirect lighting to resolve.
"""

import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

sys.path.insert(0, str(HERE))

import bpy  # noqa: E402
from mathutils import Vector  # noqa: E402

import build_rocks  # noqa: E402

OUT_PATH = HERE / "preview.png"

WIDTH = 1600
HEIGHT = 500
GAP_M = 0.35  # metres between adjacent rocks' bounding boxes

CAMERA_YAW_DEG = 35.0
CAMERA_PITCH_DEG = 25.0
ORTHO_MARGIN = 1.35  # fraction of the full row width the ortho frame shows, for edge padding

GREY = (0.62, 0.62, 0.62, 1.0)
BACKGROUND = (0.96, 0.96, 0.95, 1.0)


def lay_out(objects):
    """Spaces the six objects left to right along Blender X with GAP_M between bounding
    boxes. Returns the total row width and centre X, for framing the camera."""
    cursor = 0.0
    centres = []
    for obj in objects:
        dims = obj.dimensions  # Blender-space (X, Y, Z) bbox size, post-triangulate
        half_x = dims.x / 2.0
        cursor += half_x
        obj.location.x = cursor
        centres.append(cursor)
        cursor += half_x + GAP_M
    row_width = cursor - GAP_M
    mid_x = row_width / 2.0
    return row_width, mid_x


def add_camera(row_width, mid_x, max_height):
    """Orthographic 3/4 camera: sits up and to one side of the row's centre, looking back at
    it — yaw CAMERA_YAW_DEG around the vertical (Blender Z = Unity y) axis from straight-on,
    pitched CAMERA_PITCH_DEG down from level. Framed to fit the whole row with ORTHO_MARGIN of
    padding. Built with a look-at (track-to -Z, up +Y) rather than hand-composed Euler angles,
    which is what actually pointed the camera at nothing on the first attempt here.
    """
    data = bpy.data.cameras.new("PreviewCamera")
    data.type = "ORTHO"
    data.ortho_scale = max(row_width * ORTHO_MARGIN, max_height * 2.5, 1.0)

    camera = bpy.data.objects.new("PreviewCamera", data)

    yaw = math.radians(CAMERA_YAW_DEG)
    pitch = math.radians(CAMERA_PITCH_DEG)
    distance = row_width * 3.0 + 5.0

    target = Vector((mid_x, 0.0, max_height * 0.3))
    direction = Vector((
        math.sin(yaw) * math.cos(pitch),
        -math.cos(yaw) * math.cos(pitch),
        math.sin(pitch),
    ))
    camera.location = target + direction * distance
    camera.rotation_mode = "QUATERNION"
    camera.rotation_quaternion = (-direction).to_track_quat("-Z", "Y")

    scene = bpy.context.scene
    scene.collection.objects.link(camera)
    scene.camera = camera
    scene.render.resolution_x = WIDTH
    scene.render.resolution_y = HEIGHT
    scene.render.resolution_percentage = 100
    return camera


def flat_grey_material():
    material = bpy.data.materials.new("PreviewGrey")
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    nodes.clear()

    geometry = nodes.new("ShaderNodeNewGeometry")
    sun = nodes.new("ShaderNodeVectorMath")
    sun.operation = "DOT_PRODUCT"
    sun.inputs[1].default_value = (-0.45, -0.35, 0.82)  # same "upper-left-front" light as
    #                                                      make_sheets.py's flat Lambert term
    shade = nodes.new("ShaderNodeMapRange")
    shade.inputs["To Min"].default_value = 0.55
    shade.inputs["To Max"].default_value = 1.15
    shade.inputs["From Min"].default_value = -0.2
    multiply = nodes.new("ShaderNodeMix")
    multiply.data_type = "RGBA"
    multiply.blend_type = "MULTIPLY"
    multiply.inputs["Factor"].default_value = 1.0
    multiply.inputs[6].default_value = GREY
    emission = nodes.new("ShaderNodeEmission")
    output = nodes.new("ShaderNodeOutputMaterial")

    links.new(geometry.outputs["Normal"], sun.inputs[0])
    links.new(sun.outputs["Value"], shade.inputs["Value"])
    links.new(shade.outputs["Result"], multiply.inputs[7])
    links.new(multiply.outputs[2], emission.inputs["Color"])
    links.new(emission.outputs["Emission"], output.inputs["Surface"])
    return material


def configure_cycles():
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 16
    scene.cycles.use_denoising = False
    scene.cycles.max_bounces = 0
    scene.cycles.transparent_max_bounces = 0
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"

    world = bpy.data.worlds.new("PreviewWorld")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = BACKGROUND
    scene.world = world


def main():
    objects, ok = build_rocks.build()
    if not ok:
        print("build_rocks.build() failed its own checks — see its output above.", file=sys.stderr)
        return 1

    material = flat_grey_material()
    for obj in objects:
        obj.data.materials.clear()
        obj.data.materials.append(material)

    row_width, mid_x = lay_out(objects)
    max_height = max(obj.dimensions.z for obj in objects)  # Blender Z = Unity y (up)
    add_camera(row_width, mid_x, max_height)
    configure_cycles()

    bpy.context.scene.render.filepath = str(OUT_PATH.resolve())
    bpy.ops.render.render(write_still=True)
    print(f"preview: wrote {OUT_PATH} ({OUT_PATH.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    code = main()
    if code:
        sys.exit(code)
