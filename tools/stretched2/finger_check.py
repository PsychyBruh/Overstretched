# Rig check: curl every finger into a claw and roll the eye bones, then render hand + face close-ups.
import bpy, math, sys
from mathutils import Matrix, Vector

out = sys.argv[sys.argv.index("--") + 1]
scene = bpy.context.scene
rig = bpy.data.objects["StretchedRig"]
rig.data.pose_position = "POSE"
for pb in rig.pose.bones:
    n = pb.name
    if any(n.startswith(f) for f in ("index", "middle", "ring", "pinky", "thumb")):
        pb.rotation_mode = "XYZ"
        pb.rotation_euler = (math.radians(55 if not n.startswith("thumb") else 30), 0, 0)
    if n.startswith("eye."):
        pb.rotation_mode = "XYZ"
        pb.rotation_euler = (math.radians(25), 0, math.radians(20))
for o in scene.objects:
    if o.name.startswith("Bake"):
        o.hide_render = True
clay = bpy.data.materials.new("Clay")
clay.use_nodes = True
clay.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.55, 0.53, 0.52, 1)
for o in scene.objects:
    if o.type == "MESH":
        o.data.materials.clear()
        o.data.materials.append(clay)
world = bpy.data.worlds.new("W")
world.color = (0.06, 0.06, 0.07)
scene.world = world
bpy.context.view_layer.update()


def bone_world(name, tail=False):
    pb = rig.pose.bones[name]
    return rig.matrix_world @ (pb.tail if tail else pb.head)


cam = bpy.data.objects.new("C", bpy.data.cameras.new("C"))
scene.collection.objects.link(cam)
scene.camera = cam
lamp = bpy.data.objects.new("K", bpy.data.lights.new("K", "AREA"))
lamp.data.energy, lamp.data.size = 80, 1
scene.collection.objects.link(lamp)
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = scene.render.resolution_y = 600
for label, target, offset, lens in (
        ("hand", bone_world("hand.R").lerp(bone_world("hand.R", True), 0.5), Vector((-0.25, -0.45, 0.1)), 50),
        ("face", bone_world("head").lerp(bone_world("head", True), 0.35), Vector((0.1, -0.5, 0.0)), 70)):
    cam.location = target + offset
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = lens
    lamp.location = target + offset + Vector((0.3, 0, 0.4))
    lamp.rotation_euler = (target - lamp.location).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = f"{out}_{label}.png"
    bpy.ops.render.render(write_still=True)
