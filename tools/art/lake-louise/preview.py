"""Render the Lake Louise backdrop from the game camera, for checking against the concept.

Usage:
    python3 tools/art/lake-louise/preview.py <out.png> [--mode geometry|shaded] [--width 1920]

geometry  plain vertex colours under a flat studio light - is the SHAPE right?
shaded    the look the Unity shaders are meant to produce (shaders.py) - is the LOOK right?

The camera is AimPreview's: position Unity (0, 2, -3), vertical FOV 60, pitched down so the
horizon sits a third of the way down a 16:9 frame (PitchForHorizonFraction). Blender Y is
Unity z, so that is Blender location (0, -3, 2).
"""

import argparse
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import bpy  # noqa: E402

import build_lake_louise  # noqa: E402

VERTICAL_FOV_DEGREES = 60.0
HORIZON_SCREEN_FRACTION = 1.0 / 3.0


def pitch_for_horizon_fraction():
    """Same maths as AimPreview.PitchForHorizonFraction, in degrees."""
    offset_from_centre = (0.5 - HORIZON_SCREEN_FRACTION) * 2.0
    half_fov = math.radians(VERTICAL_FOV_DEGREES * 0.5)
    return math.degrees(math.atan(offset_from_centre * math.tan(half_fov)))


def add_game_camera(width):
    scene = bpy.context.scene
    data = bpy.data.cameras.new("GameCamera")
    data.sensor_fit = "VERTICAL"
    data.angle_y = math.radians(VERTICAL_FOV_DEGREES)
    data.clip_start = 0.3
    data.clip_end = 1000.0
    camera = bpy.data.objects.new("GameCamera", data)
    camera.location = (0.0, -3.0, 2.0)
    # Blender cameras look down -Z; 90 degrees about X turns that to +Y (down the lake).
    camera.rotation_euler = (math.radians(90.0 - pitch_for_horizon_fraction()), 0.0, 0.0)
    scene.collection.objects.link(camera)
    scene.camera = camera
    scene.render.resolution_x = width
    scene.render.resolution_y = round(width * 9 / 16)
    scene.render.resolution_percentage = 100
    return camera


def geometry_look():
    """Workbench-free: Cycles with the vertex colour as unlit emission plus a simple sun term.

    Workbench and EEVEE need a GPU context the cloud container does not have; Cycles on the CPU
    does not, and with emission-only materials it converges in a handful of samples.
    """
    material = bpy.data.materials.new("GeometryPreview")
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    nodes.clear()
    colour = nodes.new("ShaderNodeVertexColor")
    colour.layer_name = "Col"
    geometry = nodes.new("ShaderNodeNewGeometry")
    sun = nodes.new("ShaderNodeVectorMath")
    sun.operation = "DOT_PRODUCT"
    sun.inputs[1].default_value = (-0.45, -0.35, 0.82)
    shade = nodes.new("ShaderNodeMapRange")
    shade.inputs["To Min"].default_value = 0.55
    shade.inputs["To Max"].default_value = 1.15
    shade.inputs["From Min"].default_value = -0.2
    multiply = nodes.new("ShaderNodeMix")
    multiply.data_type = "RGBA"
    multiply.blend_type = "MULTIPLY"
    multiply.inputs["Factor"].default_value = 1.0
    emission = nodes.new("ShaderNodeEmission")
    output = nodes.new("ShaderNodeOutputMaterial")
    links.new(geometry.outputs["Normal"], sun.inputs[0])
    links.new(sun.outputs["Value"], shade.inputs["Value"])
    links.new(colour.outputs["Color"], multiply.inputs[6])
    links.new(shade.outputs["Result"], multiply.inputs[7])
    links.new(multiply.outputs[2], emission.inputs["Color"])
    links.new(emission.outputs["Emission"], output.inputs["Surface"])
    for obj in bpy.context.scene.objects:
        if obj.type == "MESH":
            obj.data.materials.clear()
            obj.data.materials.append(material)

    world = bpy.data.worlds.new("GeometryWorld")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.6, 0.72, 0.85, 1.0)
    bpy.context.scene.world = world

    water = bpy.data.meshes.new("WaterPlane")
    size = 1000.0
    water.from_pydata([(-size, -60, 0), (size, -60, 0), (size, 1000, 0), (-size, 1000, 0)], [], [(0, 1, 2, 3)])
    water_object = bpy.data.objects.new("WaterPlane", water)
    water_material = bpy.data.materials.new("WaterFlat")
    water_material.use_nodes = True
    water_nodes = water_material.node_tree.nodes
    water_nodes.clear()
    flat = water_nodes.new("ShaderNodeEmission")
    flat.inputs["Color"].default_value = (0.05, 0.36, 0.40, 1.0)
    water_output = water_nodes.new("ShaderNodeOutputMaterial")
    water_material.node_tree.links.new(flat.outputs[0], water_output.inputs["Surface"])
    water.materials.append(water_material)
    bpy.context.scene.collection.objects.link(water_object)


def configure_cycles(samples):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = samples
    scene.cycles.use_denoising = False
    scene.cycles.max_bounces = 0
    scene.cycles.transparent_max_bounces = 8
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("out", type=Path)
    parser.add_argument("--mode", choices=("geometry", "shaded"), default="geometry")
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--samples", type=int, default=16)
    args = parser.parse_args([a for a in sys.argv[1:] if a != "--"])

    objects = build_lake_louise.build()
    failures = build_lake_louise.report_budget(objects)
    if failures:
        print(f"OVER BUDGET: {', '.join(failures)}", file=sys.stderr)
        return 1

    add_game_camera(args.width)
    if args.mode == "geometry":
        geometry_look()
    else:
        import shaders
        shaders.apply(objects)
    configure_cycles(args.samples)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    bpy.context.scene.render.filepath = str(args.out.resolve())
    bpy.ops.render.render(write_still=True)
    print(f"preview: wrote {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
