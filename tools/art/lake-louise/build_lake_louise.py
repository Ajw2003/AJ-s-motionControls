"""Build the Lake Louise backdrop from the SVG sources and export it for Unity.

Run either way - the script is the same:
    python3 tools/art/lake-louise/build_lake_louise.py            (bpy 4.5 Python module)
    blender --background --python tools/art/lake-louise/build_lake_louise.py

Reads   tools/art/lake-louise/svg/{valley-plan,photo-silhouettes,pine-profile,boathouse-elevation}.svg
Writes  Assets/Environments/LakeLouise/LakeLouise.fbx

Coordinates. Everything is built in Blender with X = Unity x (right), Y = Unity z (down the
lake, away from the camera) and Z = Unity y (up), metres, water at Z = 0. The FBX export maps
Blender (X, Y, Z) to Unity (x, z, y); see export_fbx for why that is the setting it is, and
for the check the Unity side runs so a wrong mapping cannot pass silently.

Objects exported (all vertex-coloured, one shared material slot, no textures):
    Valley          terrain: both side ridges, the far glacier wall, shores and lake bed
    Forest_Left     pines on x < 0
    Forest_Right    pines on x >= 0 up to the far end
    Forest_Far      pines at the far end of the valley
    Boathouse       walls, roof and canoes

The script prints a triangle count per object and exits non-zero if any is over budget.
"""

import math
import random
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

import bpy  # before bmesh: the bpy module build only exposes bmesh once bpy is loaded
import bmesh
import numpy as np
from mathutils import Vector, noise

HERE = Path(__file__).resolve().parent
SVG_DIR = HERE / "svg"
REPO = HERE.parents[2]
FBX_PATH = REPO / "Assets" / "Environments" / "LakeLouise" / "LakeLouise.fbx"

SEED = 20260927

# --- Terrain -----------------------------------------------------------------------------------
GRID_SPACING = 4.5              # metres between terrain samples before decimation
GRID_JITTER = 1.5               # random offset per sample, so the low-poly facets are irregular
GRID_X = (-760.0, 760.0)
GRID_Y = (-60.0, 1000.0)        # nothing behind the camera is ever seen; far clip is 1000
VALLEY_TRIANGLE_BUDGET = 16000

LAKE_BED_SLOPE = 0.15           # metres of depth per metre in from the shore
SHORE_HEIGHT = 0.8              # land height right at the waterline
SHORE_SLOPE = 0.10              # how fast the shore rises away from the water
SHORE_MAX_RISE = 1.2            # steepest rise off any shoreline (~50 degrees): no sheer walls

CREST_NOISE_SCALE = 0.012       # jaggedness of crests and faces
CREST_NOISE_AMPLITUDE = 0.02    # as a fraction of local height - small, so crests keep the photo's line
CAMERA_HEIGHT = 2.0             # AimPreview's camera y; crest heights are sighted from here
CAMERA_Z = -3.0

# --- Colour zones (heights in metres above the water) ------------------------------------------
TREELINE_SIDES = 85.0           # forest stops here on the side ridges...
TREELINE_FAR = 8.0              # ...and much lower under the glacier
SNOWLINE_FAR = 40.0             # the far wall is white above this
SNOWLINE_SIDES = 190.0          # side ridges are summer rock (no snow in the photo)
FOREST_FAR_START_Y = 750.0      # pines past this go in Forest_Far (grouping only, not colour)
STEEP_NORMAL_Z = 0.50           # faces steeper than this are rock, whatever their height

# sRGB, as picked from the reference photo - written with color_srgb, never .color (linear).
PALETTE = {
    "forest": (0.24, 0.30, 0.14),
    "forest_far": (0.20, 0.26, 0.24),
    "rock": (0.54, 0.53, 0.51),
    "rock_warm": (0.58, 0.54, 0.48),
    "rock_blue": (0.36, 0.40, 0.48),
    "snow": (0.93, 0.95, 0.98),
    "shore": (0.49, 0.48, 0.44),
    "lake_bed": (0.44, 0.70, 0.66),
    "pine": (0.18, 0.25, 0.11),
    "boathouse_walls": (0.42, 0.29, 0.20),
    "boathouse_roof": (0.54, 0.56, 0.63),
    "canoe": (0.71, 0.29, 0.24),
}

# --- Forest ------------------------------------------------------------------------------------
PINE_SIDES = 5
PINE_TARGET_COUNT = 1300
PINE_SCALE = (0.65, 1.15)
PINE_MAX_Y = 900.0
FOREST_TRIANGLE_BUDGET = 36000

# --- Everything together -----------------------------------------------------------------------
TOTAL_TRIANGLE_BUDGET = 55000   # the alpine-lake plan allows ~80k including reflections;
                                # reflections re-draw the same meshes, so half of that each


# =============================================================================================
# SVG reading
# =============================================================================================

SVG_NS = "{http://www.w3.org/2000/svg}"


def parse_points(text):
    numbers = [float(n) for n in re.findall(r"-?\d+(?:\.\d+)?", text)]
    return [(numbers[i], numbers[i + 1]) for i in range(0, len(numbers), 2)]


def translate_of(element):
    """Only translate() is used in these files; anything else is refused, not ignored."""
    transform = element.get("transform")
    if not transform:
        return 0.0, 0.0
    match = re.fullmatch(r"\s*translate\(\s*(-?[\d.]+)[ ,]+(-?[\d.]+)\s*\)\s*", transform)
    if not match:
        raise ValueError(f"unsupported transform {transform!r} on {element.get('id')}")
    return float(match.group(1)), float(match.group(2))


def load_svg(name):
    """Returns {id: (element, points)} in each element's LOCAL coordinates.

    Transforms are deliberately not applied: every data element in these files is drawn in the
    coordinates it means. Only translate() is allowed anywhere (translate_of refuses anything
    else), so nothing rotated or scaled can be misread.
    """
    tree = ElementTree.parse(SVG_DIR / name)
    found = {}
    for element in tree.getroot().iter():
        translate_of(element)
        element_id = element.get("id")
        if not element_id or "guide" in (element.get("class") or ""):
            continue
        tag = element.tag.replace(SVG_NS, "")
        if tag in ("polyline", "polygon"):
            found[element_id] = (element, parse_points(element.get("points")))
        elif tag == "circle":
            found[element_id] = (element, [(float(element.get("cx")), float(element.get("cy")))])
    return found


def require(shapes, element_id, source):
    if element_id not in shapes:
        raise KeyError(f"{source} has no element with id={element_id!r}")
    return shapes[element_id]


# =============================================================================================
# Terrain
# =============================================================================================

def plan_to_world(points):
    """valley-plan.svg draws z UP the page, so SVG y is -world z (= -Blender Y)."""
    return np.array([(x, -y) for x, y in points], dtype=np.float64)


def load_photo_silhouettes():
    """photo-silhouettes.svg -> {silhouette id: (azimuths, elevations)} in degrees, game-ready.

    Each traced photo pixel becomes a direction: azimuth from its column, elevation from its
    height above the waterline, both through the photo's assumed focal length. Elevation is then
    multiplied by data-elevation-scale so the tallest summit fits the game frame; see the
    header of photo-silhouettes.svg for why that compromise exists.
    """
    root = ElementTree.parse(SVG_DIR / "photo-silhouettes.svg").getroot()
    width = float(root.get("width"))
    focal = (width / 2.0) / math.tan(math.radians(float(root.get("data-hfov")) / 2.0))
    scale = float(root.get("data-elevation-scale"))
    waterline = next(float(e.get("data-y")) for e in root.iter() if e.get("id") == "waterline")

    silhouettes = {}
    for element_id, (_, points) in load_svg("photo-silhouettes.svg").items():
        pixels = np.array(sorted(points))
        azimuths = np.degrees(np.arctan((pixels[:, 0] - width / 2.0) / focal))
        elevations = np.degrees(np.arctan((waterline - pixels[:, 1]) / focal)) * scale
        silhouettes[element_id] = (azimuths, np.maximum(elevations, 0.0))
    return silhouettes


def ridge_samples(plan, silhouettes, ridge_id):
    """Dense crest samples along one ridge, each as high as the photo's silhouette says.

    A crest sample at horizontal distance d from the camera, in a direction where the photo's
    (scaled) skyline stands e degrees up, gets height CAMERA_HEIGHT + d * tan(e), so it lands
    exactly on that skyline. Directions past either end of a traced line take its end value,
    which is how the ridges carry on beyond the photo's edges.

    The crest dominates the skyline because every data-slope (0.6-1.15) is steeper than any
    sight line to a crest (tan of at most ~15 degrees, 0.27): ground in front of a crest,
    along the same sight line, always drops away below it.
    """
    element, points = require(plan, ridge_id, "valley-plan.svg")
    crest = plan_to_world(points)
    silhouette_id = element.get("data-silhouette")
    if silhouette_id not in silhouettes:
        raise KeyError(f"photo-silhouettes.svg has no element with id={silhouette_id!r}")
    azimuths, elevations = silhouettes[silhouette_id]
    slope = float(element.get("data-slope"))

    samples = []
    for a, b in zip(crest[:-1], crest[1:]):
        steps = max(2, int(np.linalg.norm(b - a) / 4.0))
        for t in np.linspace(0.0, 1.0, steps, endpoint=False):
            samples.append(a + (b - a) * t)
    samples.append(crest[-1])
    samples = np.array(samples)

    forward = samples[:, 1] - CAMERA_Z
    distance = np.hypot(samples[:, 0], forward)
    azimuth = np.degrees(np.arctan2(samples[:, 0], forward))
    elevation = np.interp(azimuth, azimuths, elevations)
    heights = CAMERA_HEIGHT + distance * np.tan(np.radians(elevation))
    return samples, heights, slope


def polygon_inside_and_distance(points_xy, polygon):
    """Ray-cast inside test and distance to the polygon edge, vectorised over points."""
    x, y = points_xy[:, 0], points_xy[:, 1]
    inside = np.zeros(len(points_xy), dtype=bool)
    distance = np.full(len(points_xy), np.inf)
    for (x1, y1), (x2, y2) in zip(polygon, np.roll(polygon, -1, axis=0)):
        crosses = ((y1 > y) != (y2 > y)) & (x < (x2 - x1) * (y - y1) / (y2 - y1 + 1e-12) + x1)
        inside ^= crosses
        edge = np.array([x2 - x1, y2 - y1])
        t = np.clip(((x - x1) * edge[0] + (y - y1) * edge[1]) / (edge @ edge), 0.0, 1.0)
        distance = np.minimum(distance, np.hypot(x - (x1 + t * edge[0]), y - (y1 + t * edge[1])))
    return inside, distance


RIDGES = ("ridge-left", "ridge-right", "ridge-far")
FAR_RIDGE = RIDGES.index("ridge-far")


def ridge_tents(points_xy, plan, silhouettes):
    """Height of each ridge's tent over each point: shape (len(RIDGES), len(points)).

    A tent sits under every crest sample; a ridge's surface is the highest of its tents. Done
    in chunks so the (points x samples) distance matrix stays small.
    """
    tents = np.full((len(RIDGES), len(points_xy)), -30.0)
    for index, ridge_id in enumerate(RIDGES):
        samples, crest_heights, slope = ridge_samples(plan, silhouettes, ridge_id)
        for start in range(0, len(points_xy), 4096):
            chunk = points_xy[start:start + 4096]
            d = np.hypot(chunk[:, None, 0] - samples[None, :, 0], chunk[:, None, 1] - samples[None, :, 1])
            tents[index, start:start + 4096] = (crest_heights[None, :] - slope * d).max(axis=1)
    return tents


def terrain_height(points_xy, plan, silhouettes, lake):
    height = np.maximum(ridge_tents(points_xy, plan, silhouettes).max(axis=0), -30.0)

    # Rugged faces: noise scaled by the height, so peaks break up and the lake shore stays calm.
    rugged = np.array([
        noise.fractal(Vector((x * CREST_NOISE_SCALE, y * CREST_NOISE_SCALE, 0.37)), 0.9, 2.1, 5)
        for x, y in points_xy
    ])
    height += rugged * CREST_NOISE_AMPLITUDE * np.maximum(height, 0.0) + rugged * 1.5

    inside, distance = polygon_inside_and_distance(points_xy, lake)
    lake_bed = np.maximum(-1.5 - LAKE_BED_SLOPE * distance, -30.0)
    shore = SHORE_HEIGHT + SHORE_SLOPE * distance
    # Land may not rise faster than SHORE_MAX_RISE off the water, or a shoreline cut across a
    # slope becomes a vertical wall.
    land = np.maximum(np.minimum(height, SHORE_HEIGHT + SHORE_MAX_RISE * distance), shore)
    return np.where(inside, np.minimum(height, lake_bed), land)


# Where the camera can stand: the throw position, and the chase camera's reach down the lake.
VISIBILITY_EYES = ((0.0, CAMERA_Z, CAMERA_HEIGHT), (0.0, 60.0, CAMERA_HEIGHT))
VISIBILITY_MARGIN_DEGREES = 1.0


def visible_region(x, y, heights):
    """True for samples some camera position can see; the rest is culled before decimation.

    Most of the grid is the far side of a ridge, which no camera ever sees. Left in, it took
    most of the triangle budget and decimation rounded the visible crests off (the right
    summit came out 20-40 px under the photo's line). A sample is hidden from an eye when
    something nearer in the same direction stands higher in the view, by more than a margin.
    """
    visible = np.zeros(len(x), dtype=bool)
    for eye_x, eye_y, eye_z in VISIBILITY_EYES:
        dx, dy = x - eye_x, y - eye_y
        distance = np.hypot(dx, dy)
        in_front = dy > 1.0
        azimuth_bin = np.round(np.degrees(np.arctan2(dx, dy)) * 2.0).astype(int)   # half degrees
        elevation = np.degrees(np.arctan2(heights - eye_z, np.maximum(distance, 1e-3)))
        seen = np.zeros(len(x), dtype=bool)
        order = np.lexsort((distance, azimuth_bin))            # by direction, then nearest first
        bins, starts = np.unique(azimuth_bin[order], return_index=True)
        for start, end in zip(starts, list(starts[1:]) + [len(order)]):
            ray = order[start:end]
            horizon_before = np.maximum.accumulate(np.concatenate(([-90.0], elevation[ray][:-1])))
            seen[ray] = elevation[ray] >= horizon_before - VISIBILITY_MARGIN_DEGREES
        visible |= seen & in_front
    return visible


def build_valley(plan, silhouettes, rng):
    _, lake_points = require(plan, "lake", "valley-plan.svg")
    lake = plan_to_world(lake_points)

    xs = np.arange(GRID_X[0], GRID_X[1] + 0.1, GRID_SPACING)
    ys = np.arange(GRID_Y[0], GRID_Y[1] + 0.1, GRID_SPACING)
    gx, gy = np.meshgrid(xs, ys)
    jitter = rng.uniform(-GRID_JITTER, GRID_JITTER, size=(2,) + gx.shape)
    edge = (gx == xs[0]) | (gx == xs[-1]) | (gy == ys[0]) | (gy == ys[-1])
    gx = np.where(edge, gx, gx + jitter[0])
    gy = np.where(edge, gy, gy + jitter[1])
    points = np.stack([gx.ravel(), gy.ravel()], axis=1)
    heights = terrain_height(points, plan, silhouettes, lake)

    columns = len(xs)
    keep = visible_region(points[:, 0], points[:, 1], heights)
    print(f"Valley: {keep.sum()} of {len(keep)} terrain samples visible from a camera position")
    faces = []
    for row in range(len(ys) - 1):
        for column in range(columns - 1):
            a = row * columns + column
            b, c, d = a + 1, a + columns + 1, a + columns
            if keep[a] or keep[b] or keep[c] or keep[d]:
                # Alternate the diagonal so the facets do not all lean one way.
                if (row + column) % 2:
                    faces += [(a, b, c), (a, c, d)]
                else:
                    faces += [(a, b, d), (b, c, d)]

    vertices = [(x, y, h) for (x, y), h in zip(points, heights)]
    mesh = bpy.data.meshes.new("Valley")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new("Valley", mesh)
    bpy.context.scene.collection.objects.link(obj)

    remove_unused_vertices(obj)
    decimate_to(obj, VALLEY_TRIANGLE_BUDGET)
    colour_valley(obj, plan, silhouettes)
    return obj, lake


def remove_unused_vertices(obj):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bm.to_mesh(obj.data)
    bm.free()


def decimate_to(obj, triangle_budget):
    triangles = len(obj.data.polygons)
    if triangles <= triangle_budget:
        return
    modifier = obj.modifiers.new("Decimate", "DECIMATE")
    modifier.decimate_type = "COLLAPSE"
    modifier.ratio = triangle_budget / triangles * 0.98
    modifier.use_collapse_triangulate = True
    with bpy.context.temp_override(object=obj, active_object=obj):
        bpy.ops.object.modifier_apply(modifier=modifier.name)


def face_colour(centre, normal_z, far_end, rng):
    """far_end: this ground belongs to the glacier wall (its tent is the highest here).

    Decided by which ridge shapes the ground, not by a distance down the lake: the inner ends
    of both side ridges reach as far as the glacier wall's foot, and a z cut-off either paints
    their cliffs white or grows forest over the glacier's scree.
    """
    x, y, h = centre
    treeline = TREELINE_FAR if far_end else TREELINE_SIDES
    snowline = SNOWLINE_FAR if far_end else SNOWLINE_SIDES
    # A ragged line rather than a contour: noise shifts each zone boundary by tens of metres.
    wobble = noise.noise(Vector((x * 0.02, y * 0.02, 1.7))) * 18.0
    variation = rng.uniform(-0.035, 0.035)

    if h < 0.0:
        base = PALETTE["lake_bed"]
    elif h < SHORE_HEIGHT + 2.0:
        base = PALETTE["shore"]
    elif h > snowline + wobble and normal_z > 0.35:
        base = PALETTE["snow"]
    elif normal_z < STEEP_NORMAL_Z or h > treeline + wobble:
        if far_end:
            # The foot of the glacier wall is a scree fan in the photo, not a cliff.
            base = PALETTE["rock_blue"] if h > 25.0 else PALETTE["shore"]
        else:
            base = PALETTE["rock_warm"] if noise.noise(Vector((x * 0.01, y * 0.01, 4.2))) > 0.1 else PALETTE["rock"]
    else:
        base = PALETTE["forest_far"] if far_end else PALETTE["forest"]
    return tuple(min(1.0, max(0.0, c + variation)) for c in base)


def zone_of(colour):
    for name, value in PALETTE.items():
        if all(abs(a - b) <= 0.036 for a, b in zip(colour, value)):
            return name
    return None


def colour_valley(obj, plan, silhouettes):
    rng = random.Random(SEED + 1)
    mesh = obj.data
    layer = mesh.color_attributes.new("Col", "BYTE_COLOR", "CORNER")
    centres = np.array([(p.center.x, p.center.y) for p in mesh.polygons])
    owners = ridge_tents(centres, plan, silhouettes).argmax(axis=0)
    zones = []
    for polygon, owner in zip(mesh.polygons, owners):
        colour = face_colour(polygon.center, polygon.normal.z, owner == FAR_RIDGE, rng)
        zones.append(zone_of(colour) or "")
        for loop_index in polygon.loop_indices:
            layer.data[loop_index].color_srgb = (*colour, 1.0)
    obj["face_zones"] = zones


# =============================================================================================
# Forest
# =============================================================================================

def pine_template():
    """Spins pine-profile.svg's half-silhouette into a PINE_SIDES-sided tree."""
    shapes = load_svg("pine-profile.svg")
    _, profile = require(shapes, "pine-profile", "pine-profile.svg")
    rings = [(radius, -y) for radius, y in profile]   # SVG y is down; height is up
    vertices, faces = [], []
    apex = rings[0]
    vertices.append((0.0, 0.0, apex[1]))
    previous = [0] * PINE_SIDES
    for radius, height in rings[1:]:
        ring = []
        for side in range(PINE_SIDES):
            angle = 2.0 * math.pi * side / PINE_SIDES
            ring.append(len(vertices))
            vertices.append((radius * math.cos(angle), radius * math.sin(angle), height))
        for side in range(PINE_SIDES):
            nxt = (side + 1) % PINE_SIDES
            if previous[side] == previous[nxt]:
                faces.append((previous[side], ring[side], ring[nxt]))
            else:
                faces.append((previous[side], ring[side], ring[nxt]))
                faces.append((previous[side], ring[nxt], previous[nxt]))
        previous = ring
    return vertices, faces


def scatter_forest(valley, rng, clearing_centre, clearing_radius):
    """Pines on forest faces, none within clearing_radius of clearing_centre (the boathouse)."""
    template_vertices, template_faces = pine_template()
    mesh = valley.data
    zones = list(valley["face_zones"])

    candidates = []
    for polygon, zone in zip(mesh.polygons, zones):
        cleared = (polygon.center.xy - clearing_centre.xy).length < clearing_radius
        if zone in ("forest", "forest_far") and polygon.center.y < PINE_MAX_Y and not cleared:
            candidates.append((polygon, polygon.area))
    if not candidates:
        raise RuntimeError("no forest faces to scatter pines on - check the colour zones")

    areas = np.array([area for _, area in candidates])
    weights = areas / areas.sum()
    chosen = np.random.default_rng(SEED + 2).choice(len(candidates), size=PINE_TARGET_COUNT, p=weights)

    groups = {"Forest_Left": ([], []), "Forest_Right": ([], []), "Forest_Far": ([], [])}
    for index in chosen:
        polygon = candidates[index][0]
        corners = [mesh.vertices[v].co for v in polygon.vertices]
        a, b = sorted((rng.random(), rng.random()))
        position = corners[0] * a + corners[1] * (b - a) + corners[2] * (1.0 - b)
        scale = rng.uniform(*PINE_SCALE)
        turn = rng.uniform(0.0, 2.0 * math.pi)
        cos_t, sin_t = math.cos(turn), math.sin(turn)
        shade = rng.uniform(-0.03, 0.03)
        colour = tuple(max(0.0, c + shade) for c in PALETTE["pine"])

        name = "Forest_Far" if position.y > FOREST_FAR_START_Y else (
            "Forest_Left" if position.x < 0 else "Forest_Right")
        vertices, faces = groups[name]
        base = len(vertices)
        for x, y, z in template_vertices:
            vertices.append((
                position.x + (x * cos_t - y * sin_t) * scale,
                position.y + (x * sin_t + y * cos_t) * scale,
                position.z + z * scale,
                colour,
            ))
        faces.extend(tuple(base + i for i in face) for face in template_faces)

    objects = []
    for name, (vertices, faces) in groups.items():
        objects.append(make_coloured_object(name, vertices, faces))
    return objects


def make_coloured_object(name, vertices, faces):
    """vertices carry their colour as a 4th element; colour is written per face corner."""
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([v[:3] for v in vertices], [], faces)
    mesh.update()
    layer = mesh.color_attributes.new("Col", "BYTE_COLOR", "CORNER")
    for polygon in mesh.polygons:
        for loop_index in polygon.loop_indices:
            colour = vertices[mesh.loops[loop_index].vertex_index][3]
            layer.data[loop_index].color_srgb = (*colour, 1.0)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


# =============================================================================================
# Boathouse
# =============================================================================================

def extrude_gable(outline, depth, colour, origin, facing):
    """Extrudes an SVG elevation (x across, up = -svg y) along the local depth axis.

    facing is the world direction the gable end looks toward; the extrusion runs away from it.
    """
    across = Vector((-facing.y, facing.x, 0.0))
    front = [origin + across * x + Vector((0, 0, -y)) for x, y in outline]
    back = [p - facing * depth for p in front]
    count = len(outline)
    vertices = [(*p, colour) for p in front + back]
    faces = [tuple(range(count - 1, -1, -1)), tuple(range(count, 2 * count))]
    for i in range(count):
        j = (i + 1) % count
        faces.append((i, j, count + j, count + i))
    return vertices, faces


def boathouse_marker(plan):
    """(site, scale, clearing radius) from the boathouse circle in valley-plan.svg."""
    element, marker = require(plan, "boathouse", "valley-plan.svg")
    site = Vector((marker[0][0], -marker[0][1], 0.0))
    return site, float(element.get("data-scale", "1")), float(element.get("data-clearing", "0"))


def build_boathouse(plan, valley):
    shapes = load_svg("boathouse-elevation.svg")
    site, scale, _ = boathouse_marker(plan)
    site.z = ground_height(valley, site.x, site.y)
    # Gable ends look along the shore (+/-x), so the long side and the roof face the camera,
    # as in the photo.
    facing = Vector((1.0, 0.0, 0.0))

    vertices, faces = [], []

    def add(part):
        part_vertices, part_faces = part
        base = len(vertices)
        vertices.extend(part_vertices)
        faces.extend(tuple(base + i for i in face) for face in part_faces)

    walls_element, walls = require(shapes, "boathouse-walls", "boathouse-elevation.svg")
    roof_element, roof = require(shapes, "boathouse-roof", "boathouse-elevation.svg")
    walls = [(x * scale, y * scale) for x, y in walls]
    roof = [(x * scale, y * scale) for x, y in roof]
    walls_depth = float(walls_element.get("data-depth")) * scale
    roof_depth = float(roof_element.get("data-depth")) * scale
    overhang = (roof_depth - walls_depth) * 0.5
    add(extrude_gable(walls, walls_depth, PALETTE["boathouse_walls"], site, facing))
    add(extrude_gable(roof, roof_depth, PALETTE["boathouse_roof"], site + facing * overhang, facing))

    canoe_element, canoe = require(shapes, "canoe", "boathouse-elevation.svg")
    canoe = [((x - 2.5) * scale, y * scale) for x, y in canoe]   # centred on its length
    canoe_height = float(canoe_element.get("data-height")) * scale
    for i in range(int(canoe_element.get("data-count"))):
        # Moored in a row in front of the boathouse, lying ACROSS the view: end-on to the
        # camera they were a one-pixel sliver.
        centre = site + Vector((6.0, -4.0 - i * 1.6, 0.0)) * scale
        outline = [Vector((centre.x + x, centre.y + y, 0.0)) for x, y in canoe]
        ring = [(p.x, p.y, 0.05) for p in outline]
        top = [(p.x, p.y, 0.05 + canoe_height) for p in outline]
        base = len(vertices)
        count = len(ring)
        vertices.extend((*p, PALETTE["canoe"]) for p in ring + top)
        faces.append(tuple(base + count + k for k in range(count)))
        for k in range(count):
            m = (k + 1) % count
            faces.append((base + k, base + m, base + count + m, base + count + k))

    boathouse = make_coloured_object("Boathouse", vertices, faces)
    # extrude_gable's winding depends on which way round the SVG outline was drawn, and the
    # walls came out inside-out: invisible in Blender, which draws both sides, but culled away
    # in Unity. Every part here is a closed prism (the canoes are open underneath, facing the
    # water), so recomputing outward normals is reliable.
    bm = bmesh.new()
    bm.from_mesh(boathouse.data)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(boathouse.data)
    bm.free()
    return boathouse


def ground_height(obj, x, y):
    origin = Vector((x, y, 1000.0))
    hit, location, _, _ = obj.ray_cast(origin, Vector((0.0, 0.0, -1.0)))
    if not hit:
        raise RuntimeError(f"no ground under ({x}, {y}) - is the marker inside the terrain?")
    return location.z


# =============================================================================================
# Budget, export
# =============================================================================================

def triangle_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def report_budget(objects):
    print("\nTriangles per object:")
    total = 0
    for obj in objects:
        count = triangle_count(obj)
        total += count
        print(f"  {obj.name:14s} {count:7d}")
    forest = sum(triangle_count(o) for o in objects if o.name.startswith("Forest"))
    print(f"  {'forest total':14s} {forest:7d}   (budget {FOREST_TRIANGLE_BUDGET})")
    print(f"  {'TOTAL':14s} {total:7d}   (budget {TOTAL_TRIANGLE_BUDGET})")
    failures = []
    if triangle_count(bpy.data.objects["Valley"]) > VALLEY_TRIANGLE_BUDGET:
        failures.append("Valley")
    if forest > FOREST_TRIANGLE_BUDGET:
        failures.append("forest")
    if total > TOTAL_TRIANGLE_BUDGET:
        failures.append("total")
    return failures


def flat_shade(objects):
    for obj in objects:
        for polygon in obj.data.polygons:
            polygon.use_smooth = False


def export_fbx(objects):
    """FBX for Unity.

    axis_forward='Z', axis_up='Y' with the transform baked into the vertices: Blender Y
    (down the lake) becomes FBX +Z and Blender Z becomes FBX +Y, which forces X to flip to
    stay right-handed; Unity then flips X back when it converts to left-handed. Net result:
    Blender (X, Y, Z) -> Unity (x, z, y) with no root rotation. Blender's default (-Z forward)
    is the famous "model imports turned 180 degrees" setting.

    This is reasoning, not a Unity run. LakeLouiseEnvironment.cs checks on load that the left
    massif landed at negative x and logs an error naming this function if it did not.
    """
    FBX_PATH.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=str(FBX_PATH),
        use_selection=True,
        object_types={"MESH"},
        axis_forward="Z",
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
    print(f"\nExported {FBX_PATH.relative_to(REPO)} ({FBX_PATH.stat().st_size // 1024} KB)")


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def build():
    """Builds everything into the current (emptied) scene and returns the exported objects."""
    clear_scene()
    rng = random.Random(SEED)
    np.random.seed(SEED)
    plan = load_svg("valley-plan.svg")
    silhouettes = load_photo_silhouettes()

    valley, _ = build_valley(plan, silhouettes, np.random.default_rng(SEED))
    boathouse_site, _, clearing = boathouse_marker(plan)
    forests = scatter_forest(valley, rng, boathouse_site, clearing)
    boathouse = build_boathouse(plan, valley)
    objects = [valley, *forests, boathouse]
    flat_shade(objects)
    return objects


def main():
    objects = build()
    failures = report_budget(objects)
    if failures:
        print(f"\nOVER BUDGET: {', '.join(failures)} - not exporting.", file=sys.stderr)
        return 1
    if "--no-export" not in sys.argv:
        export_fbx(objects)
    return 0


if __name__ == "__main__":
    code = main()
    if code:
        sys.exit(code)
