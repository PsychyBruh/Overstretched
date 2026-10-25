# Quick EEVEE clay check renders of whatever stage file is loaded.
# Usage: blender -b <stage.blend> -P check_render.py -- <out_prefix> <shots...>
#   shots: face, face34, body, body34, mouth
import bpy, math, sys
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
out, shots = args[0], args[1:]
scene = bpy.context.scene

clay = bpy.data.materials.new("Clay")
clay.use_nodes = True
nt = clay.node_tree
b = nt.nodes["Principled BSDF"]
b.inputs["Roughness"].default_value = 0.5
ao = nt.nodes.new("ShaderNodeAmbientOcclusion")  # darken cavities so the open mouth reads
ao.inputs["Distance"].default_value = 0.03
mixc = nt.nodes.new("ShaderNodeMix")
mixc.data_type = "RGBA"
mixc.inputs["A"].default_value = (0.12, 0.02, 0.02, 1)
mixc.inputs["B"].default_value = (0.55, 0.53, 0.52, 1)
nt.links.new(ao.outputs["AO"], mixc.inputs["Factor"])
nt.links.new(mixc.outputs["Result"], b.inputs["Base Color"])
cloth = bpy.data.materials.new("Cloth")
cloth.use_nodes = True
cloth.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.6, 0.18, 0.03, 1)
for o in scene.objects:
    if o.type == "MESH" and o.name not in ("Proxy",):
        if not o.data.materials or o.name == "Stretched":
            o.data.materials.clear()
            o.data.materials.append(cloth if "Suit" in o.name else clay)
    if o.name == "Proxy":
        o.hide_render = True

world = bpy.data.worlds.new("W")
world.color = (0.04, 0.04, 0.05)
scene.world = world


def light(name, loc, energy, size, target):
    l = bpy.data.objects.new(name, bpy.data.lights.new(name, "AREA"))
    l.data.energy, l.data.size = energy, size
    l.location = loc
    l.rotation_euler = (Vector(target) - l.location).to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(l)


# Find the figure's extent so framing works for rest and posed stages.
mesh = next(o for o in scene.objects if o.name == "Stretched")
dg = bpy.context.evaluated_depsgraph_get()
ev = mesh.evaluated_get(dg)
pts = [ev.matrix_world @ v.co for v in ev.data.vertices]
top = max(p.z for p in pts)
head_c = Vector((0, 0, 0))
headpts = [p for p in pts if p.z > top - 0.25]
head_c = sum(headpts, Vector()) / len(headpts)
H = top

cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
scene.collection.objects.link(cam)
scene.camera = cam
scene.render.engine = "BLENDER_EEVEE"
specs = {
    "face": (Vector((0, -0.7, 0.0)), 85, (700, 700), 0.0),
    "face34": (Vector((0.42, -0.55, 0.03)), 85, (700, 700), 0.0),
    "mouth": (Vector((0.1, -0.45, -0.03)), 85, (700, 700), -0.04),
    "body": (Vector((0, -H * 2.0, -H * 0.42)), 50, (600, 900), None),
    "body34": (Vector((H * 1.2, -H * 1.6, -H * 0.4)), 50, (600, 900), None),
}
light("Key", head_c + Vector((-0.8, -1.2, 0.6)), 120 if True else 0, 1.0, head_c)
light("Fill", head_c + Vector((1.0, -0.8, 0.0)), 40, 1.5, head_c)
light("BodyKey", (-2.0, -3.0, H * 0.8), 900, 3.0, (0, 0, H * 0.5))
for s in shots:
    off, lens, res, dz = specs[s]
    if dz is None:
        target = Vector((0, 0, H * 0.5))
        cam.location = target + off
    else:
        target = head_c + Vector((0, -0.05, dz))
        cam.location = head_c + off
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = lens
    scene.render.resolution_x, scene.render.resolution_y = res
    scene.render.filepath = f"{out}_{s}.png"
    bpy.ops.render.render(write_still=True)
