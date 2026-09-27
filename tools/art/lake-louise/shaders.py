"""Blender stand-ins for the Lake Louise Unity shaders, so the look can be judged in a render.

Each function here mirrors one shader under Assets/Resources/, using the same maths and the
same numbers (Assets/Environments/LakeLouise/LakeLouiseLook.json, which Unity reads too):

    backdrop_material  <-> LakeLouiseBackdrop.shader  vertex colour x (ambient + sun), then haze
    setup_sky          <-> SkyBackdrop.shader         gradient + clouds, mirrored below horizon
    add_water          <-> LakeLouiseWater.shader     turquoise body, Schlick fresnel, streaks

One deliberate difference: Unity reflects by drawing mirrored copies of the scenery under
see-through water; Blender uses a true mirror (a sharp glossy ray). For flat-lit, view-
independent shading like this the two produce the same image, apart from haze on reflections
(Unity hazes by the mirrored point's distance, Blender by the original's - slightly less).

Materials are emission-only, so Cycles needs no light sources and converges in a few samples.
"""

import json
import math
from pathlib import Path

import bpy

LOOK_PATH = Path(__file__).resolve().parents[3] / "Assets" / "Environments" / "LakeLouise" / "LakeLouiseLook.json"
LOOK = json.loads(LOOK_PATH.read_text())


def srgb_to_linear(channel):
    return channel / 12.92 if channel <= 0.04045 else ((channel + 0.055) / 1.055) ** 2.4


def colour(name):
    """look.json sRGB hex -> linear RGBA, which is what node colour sockets take."""
    value = LOOK[name].lstrip("#")
    return tuple(srgb_to_linear(int(value[i:i + 2], 16) / 255.0) for i in (0, 2, 4)) + (1.0,)


def unity_to_blender(vector):
    x, y, z = vector
    return (x, z, y)


def sun_direction():
    x, y, z = unity_to_blender(LOOK["sun_direction_towards_sun"])
    length = math.sqrt(x * x + y * y + z * z)
    return (x / length, y / length, z / length)


class Nodes:
    """Small helper so node graphs read top to bottom."""

    def __init__(self, tree):
        self.tree = tree
        self.tree.nodes.clear()

    def add(self, kind, **inputs):
        node = self.tree.nodes.new(kind)
        for key, value in inputs.items():
            node.inputs[key].default_value = value
        return node

    def math(self, operation, a, b=None):
        node = self.tree.nodes.new("ShaderNodeMath")
        node.operation = operation
        for index, value in enumerate((a, b)):
            if value is None:
                continue
            if isinstance(value, (int, float)):
                node.inputs[index].default_value = value
            else:
                self.tree.links.new(value, node.inputs[index])
        return node.outputs[0]

    def mix_colour(self, factor, a, b, blend="MIX"):
        node = self.tree.nodes.new("ShaderNodeMix")
        node.data_type = "RGBA"
        node.blend_type = blend
        for socket, value in ((node.inputs[0], factor), (node.inputs[6], a), (node.inputs[7], b)):
            if isinstance(value, (int, float)):
                socket.default_value = value
            elif isinstance(value, tuple):
                socket.default_value = value
            else:
                self.tree.links.new(value, socket)
        return node.outputs[2]

    def link(self, output, socket):
        self.tree.links.new(output, socket)


def haze(nodes, surface_colour):
    """haze = haze_max * (1 - exp(-max(0, distance - haze_start) * density)), toward haze_colour.

    The start distance matters more than the density: the forest's albedo is so dark in linear
    light that even 10% haze turns it blue-grey, and the photo's near forest has almost none.
    """
    camera = nodes.add("ShaderNodeCameraData")
    beyond = nodes.math("MAXIMUM", nodes.math("SUBTRACT", camera.outputs["View Distance"], LOOK["haze_start"]), 0.0)
    decay = nodes.math("EXPONENT", nodes.math("MULTIPLY", beyond, -LOOK["haze_density"]))
    amount = nodes.math("MULTIPLY", nodes.math("SUBTRACT", 1.0, decay), LOOK["haze_max"])
    return nodes.mix_colour(amount, surface_colour, colour("haze_colour"))


def lit(nodes, albedo):
    """albedo x (ambient + sun x max(0, N.L)) - flat faces, so N is the face normal."""
    geometry = nodes.add("ShaderNodeNewGeometry")
    dot = nodes.tree.nodes.new("ShaderNodeVectorMath")
    dot.operation = "DOT_PRODUCT"
    dot.inputs[1].default_value = sun_direction()
    nodes.link(geometry.outputs["True Normal"], dot.inputs[0])
    lambert = nodes.math("MAXIMUM", dot.outputs["Value"], 0.0)

    ambient = tuple(c * LOOK["ambient_strength"] for c in colour("ambient_colour")[:3]) + (1.0,)
    sun = tuple(c * LOOK["sun_strength"] for c in colour("sun_colour")[:3]) + (1.0,)
    light = nodes.mix_colour(1.0, ambient, nodes.mix_colour(lambert, (0, 0, 0, 1), sun), blend="ADD")
    return nodes.mix_colour(1.0, albedo, light, blend="MULTIPLY")


def backdrop_material():
    material = bpy.data.materials.new("LakeLouiseBackdrop")
    material.use_nodes = True
    nodes = Nodes(material.node_tree)
    vertex = nodes.add("ShaderNodeVertexColor")
    vertex.layer_name = "Col"
    emission = nodes.add("ShaderNodeEmission")
    nodes.link(haze(nodes, lit(nodes, vertex.outputs["Color"])), emission.inputs["Color"])
    output = nodes.add("ShaderNodeOutputMaterial")
    nodes.link(emission.outputs[0], output.inputs["Surface"])
    return material


def setup_sky():
    """World shader mirroring SkyBackdrop.shader with _MirrorBelowHorizon on.

    Direction d is the view ray. Below the horizon |d.z| is used, so a ray that bounces off the
    water and heads down still finds sky - that IS the reflected sky - and gets the reflection
    tint, as the Unity shader does.
    """
    world = bpy.data.worlds.new("LakeLouiseSky")
    world.use_nodes = True
    nodes = Nodes(world.node_tree)
    coordinates = nodes.add("ShaderNodeTexCoord")
    split = nodes.tree.nodes.new("ShaderNodeSeparateXYZ")
    nodes.link(coordinates.outputs["Generated"], split.inputs[0])
    up = nodes.math("ABSOLUTE", split.outputs["Z"])
    below = nodes.math("LESS_THAN", split.outputs["Z"], 0.0)

    gradient = nodes.math("POWER", nodes.math("MINIMUM", up, 1.0), LOOK["sky_exponent"])
    sky = nodes.mix_colour(gradient, colour("sky_horizon"), colour("sky_top"))

    # Clouds on a dome overhead: sample at d.xy / max(|d.z| + dome, 0.06). With dome = 0 this is
    # the flat cloud plane, which squashes clouds to slivers in the low band the game shows.
    safe = nodes.math("MAXIMUM", nodes.math("ADD", up, LOOK["cloud_dome_offset"]), 0.06)
    plane = nodes.tree.nodes.new("ShaderNodeCombineXYZ")
    nodes.link(nodes.math("DIVIDE", split.outputs["X"], safe), plane.inputs["X"])
    nodes.link(nodes.math("DIVIDE", split.outputs["Y"], safe), plane.inputs["Y"])
    noise = nodes.add("ShaderNodeTexNoise", Scale=LOOK["cloud_scale"], Detail=2.0, Roughness=0.5)
    nodes.link(plane.outputs[0], noise.inputs["Vector"])
    cover = nodes.tree.nodes.new("ShaderNodeMapRange")
    cover.interpolation_type = "SMOOTHSTEP"
    cover.inputs["From Min"].default_value = LOOK["cloud_cover"]
    cover.inputs["From Max"].default_value = LOOK["cloud_cover"] + 0.12
    nodes.link(noise.outputs["Fac"], cover.inputs["Value"])
    fade = nodes.tree.nodes.new("ShaderNodeMapRange")
    fade.interpolation_type = "SMOOTHSTEP"
    fade.inputs["From Min"].default_value = LOOK["cloud_horizon_fade"][0]
    fade.inputs["From Max"].default_value = LOOK["cloud_horizon_fade"][1]
    nodes.link(up, fade.inputs["Value"])
    cloud = nodes.math("MULTIPLY", nodes.math("MULTIPLY", cover.outputs["Result"], fade.outputs["Result"]), LOOK["cloud_amount"])
    sky = nodes.mix_colour(cloud, sky, colour("cloud_colour"))

    reflected = nodes.mix_colour(1.0, sky, colour("reflection_tint"), blend="MULTIPLY")
    final = nodes.mix_colour(below, sky, reflected)
    background = nodes.add("ShaderNodeBackground")
    nodes.link(final, background.inputs["Color"])
    output = nodes.add("ShaderNodeOutputWorld")
    nodes.link(background.outputs[0], output.inputs["Surface"])
    bpy.context.scene.world = world


def add_water():
    """The lake surface, mirroring LakeLouiseWater.shader.

    cos = view ray's steepness (1 looking straight down, 0 at grazing).
    F   = f0 + (1 - f0) * (1 - cos) ^ power                    Schlick fresnel
    F  *= 1 + (streak_noise - 0.5) * streak_strength * 2        wind streaks across the lake
    out = lerp(body, reflection * reflection_tint, F), then haze
    """
    mesh = bpy.data.meshes.new("LakeWater")
    # Unity's plane: 2000 m square centred 60 m down the lake (AimPreview.WaterCentreZ).
    mesh.from_pydata([(-1000, -940, 0), (1000, -940, 0), (1000, 1060, 0), (-1000, 1060, 0)], [], [(0, 1, 2, 3)])
    water = bpy.data.objects.new("LakeWater", mesh)
    bpy.context.scene.collection.objects.link(water)

    material = bpy.data.materials.new("LakeLouiseWater")
    material.use_nodes = True
    nodes = Nodes(material.node_tree)
    geometry = nodes.add("ShaderNodeNewGeometry")
    split = nodes.tree.nodes.new("ShaderNodeSeparateXYZ")
    nodes.link(geometry.outputs["Incoming"], split.inputs[0])
    cosine = nodes.math("MAXIMUM", split.outputs["Z"], 0.0)
    schlick = nodes.math("POWER", nodes.math("SUBTRACT", 1.0, cosine), LOOK["water_fresnel_power"])
    fresnel = nodes.math("ADD", LOOK["water_f0"], nodes.math("MULTIPLY", schlick, 1.0 - LOOK["water_f0"]))

    # Streaks: noise stretched across the lake (x) and squeezed along it, as wind lanes are.
    position = nodes.add("ShaderNodeNewGeometry")
    stretch = nodes.tree.nodes.new("ShaderNodeVectorMath")
    stretch.operation = "MULTIPLY"
    stretch.inputs[1].default_value = (0.004, 0.06, 0.0)
    nodes.link(position.outputs["Position"], stretch.inputs[0])
    streak_noise = nodes.add("ShaderNodeTexNoise", Scale=4.0, Detail=1.0)
    nodes.link(stretch.outputs[0], streak_noise.inputs["Vector"])
    streak = nodes.math("ADD", 1.0, nodes.math("MULTIPLY", nodes.math("SUBTRACT", streak_noise.outputs["Fac"], 0.5), LOOK["water_streak_strength"] * 2.0))
    fresnel = nodes.math("MINIMUM", nodes.math("MULTIPLY", fresnel, streak), 1.0)

    camera = nodes.add("ShaderNodeCameraData")
    far = nodes.math("MINIMUM", nodes.math("DIVIDE", camera.outputs["View Distance"], 300.0), 1.0)
    body = nodes.mix_colour(far, colour("water_body"), colour("water_body_far"))

    emission = nodes.add("ShaderNodeEmission")
    nodes.link(haze(nodes, body), emission.inputs["Color"])
    mirror = nodes.add("ShaderNodeBsdfGlossy", Roughness=0.0)
    mirror.inputs["Color"].default_value = colour("reflection_tint")
    body_opacity = nodes.math("MULTIPLY", nodes.math("SUBTRACT", 1.0, fresnel), LOOK["water_body_opacity"])
    reflection_share = nodes.math("SUBTRACT", 1.0, body_opacity)
    mix = nodes.add("ShaderNodeMixShader")
    nodes.link(reflection_share, mix.inputs[0])
    nodes.link(emission.outputs[0], mix.inputs[1])
    nodes.link(mirror.outputs[0], mix.inputs[2])
    output = nodes.add("ShaderNodeOutputMaterial")
    nodes.link(mix.outputs[0], output.inputs["Surface"])
    mesh.materials.append(material)
    return water


def apply(objects):
    material = backdrop_material()
    for obj in objects:
        obj.data.materials.clear()
        obj.data.materials.append(material)
    setup_sky()
    add_water()
    cycles = bpy.context.scene.cycles
    cycles.max_bounces = 2
    cycles.glossy_bounces = 2
    cycles.diffuse_bounces = 0
    cycles.transmission_bounces = 0
