# Clay renders of the game model in its rest pose and idle pose.
# Usage: blender -b 06_game.blend -P game_check.py -- <out_prefix>
import bpy, math, sys
from mathutils import Vector

out = sys.argv[sys.argv.index("--") + 1]
scene = bpy.context.scene
rig = bpy.data.objects["StretchedRig"]
for o in scene.objects:
    if o.name.startswith("Bake"):
        o.hide_render = True
clay = bpy.data.materials.new("Clay")
clay.use_nodes = True
clay.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.55, 0.53, 0.52, 1)
for o in scene.objects:
    if o.type == "MESH" and not o.name.startswith("Bake"):
        o.data.materials.clear()
        o.data.materials.append(clay)
world = bpy.data.worlds.new("W")
world.color = (0.05, 0.05, 0.06)
scene.world = world
l = bpy.data.objects.new("K", bpy.data.lights.new("K", "AREA"))
l.data.energy, l.data.size = 1500, 3
l.location = (-2, -4, 3)
l.rotation_euler = (Vector((0, 0, 1)) - l.location).to_track_quat("-Z", "Y").to_euler()
scene.collection.objects.link(l)
cam = bpy.data.objects.new("C", bpy.data.cameras.new("C"))
scene.collection.objects.link(cam)
scene.camera = cam
cam.location = (3.2, -5.0, 0.8)
cam.rotation_euler = (Vector((0, 0, 0.75)) - cam.location).to_track_quat("-Z", "Y").to_euler()
cam.data.lens = 40
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x, scene.render.resolution_y = 600, 800
for mode in ("REST", "POSE"):
    rig.data.pose_position = mode
    scene.render.filepath = f"{out}_{mode.lower()}.png"
    bpy.ops.render.render(write_still=True)
