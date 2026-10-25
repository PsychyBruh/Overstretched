# Step 5: horror-lit Cycles renders of The Stretched.
# Usage: blender -b 04_materials.blend -P 05_render.py -- <work_dir> <downloads_dir> <out_prefix> <samples> <scale%> <shots...>
#   shots: full, face, side
import bpy, math, os, sys, time
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:]
work, dl, out, samples, scale = args[0], args[1], args[2], int(args[3]), int(args[4])
shots = args[5:]
PH = os.path.join(dl, "polyhaven")
scene = bpy.context.scene
body = bpy.data.objects["Stretched"]
rig = bpy.data.objects["StretchedRig"]

# ---------------------------------------------------------------- render device / settings
scene.render.engine = "CYCLES"
try:
    prefs = bpy.context.preferences.addons["cycles"].preferences
    prefs.compute_device_type = "CUDA"
    prefs.get_devices()
    gpus = [d for d in prefs.devices if d.type == "CUDA"]
    for d in prefs.devices:
        d.use = True
    scene.cycles.device = "GPU" if gpus else "CPU"
    print("DEVICE", scene.cycles.device, [d.name for d in prefs.devices if d.use])
except Exception as e:
    scene.cycles.device = "CPU"
    print("DEVICE CPU (", e, ")")
scene.cycles.samples = samples
scene.cycles.adaptive_threshold = 0.02
scene.cycles.use_denoising = True
scene.cycles.max_bounces = 6
scene.cycles.caustics_reflective = scene.cycles.caustics_refractive = False
scene.view_settings.view_transform = "AgX"
try:
    scene.view_settings.look = "AgX - Medium High Contrast"
except TypeError:
    pass
scene.render.resolution_percentage = scale

# ---------------------------------------------------------------- environment
world = bpy.data.worlds.new("Facility")
world.use_nodes = True
wn = world.node_tree
bg = wn.nodes["Background"]
env = wn.nodes.new("ShaderNodeTexEnvironment")
env.image = bpy.data.images.load(os.path.join(PH, "abandoned_factory_canteen_01_2k.hdr"))
mp = wn.nodes.new("ShaderNodeMapping")
mp.inputs["Rotation"].default_value = (0, 0, math.radians(140))
tc = wn.nodes.new("ShaderNodeTexCoord")
wn.links.new(tc.outputs["Generated"], mp.inputs["Vector"])
wn.links.new(mp.outputs["Vector"], env.inputs["Vector"])
wn.links.new(env.outputs["Color"], bg.inputs["Color"])
bg.inputs["Strength"].default_value = 0.09
scene.world = world

bpy.ops.mesh.primitive_plane_add(size=30, location=(0, 0, 0))
floor = bpy.context.active_object
fm = bpy.data.materials.new("Concrete")
fm.use_nodes = True
nt = fm.node_tree
b = nt.nodes["Principled BSDF"]
uv = nt.nodes.new("ShaderNodeTexCoord")
mpf = nt.nodes.new("ShaderNodeMapping")
mpf.inputs["Scale"].default_value = (14, 14, 14)
nt.links.new(uv.outputs["UV"], mpf.inputs["Vector"])
for kind, sock, nc in (("diff", "Base Color", False), ("rough", "Roughness", True), ("nor_gl", None, True)):
    t = nt.nodes.new("ShaderNodeTexImage")
    t.image = bpy.data.images.load(os.path.join(PH, f"damaged_concrete_floor_02_{kind}_2k.jpg"))
    if nc:
        t.image.colorspace_settings.name = "Non-Color"
    nt.links.new(mpf.outputs["Vector"], t.inputs["Vector"])
    if sock:
        nt.links.new(t.outputs["Color"], b.inputs[sock])
    else:
        nm = nt.nodes.new("ShaderNodeNormalMap")
        nt.links.new(t.outputs["Color"], nm.inputs["Color"])
        nt.links.new(nm.outputs["Normal"], b.inputs["Normal"])
floor.data.materials.append(fm)

# ---------------------------------------------------------------- where the face is after posing
dg = bpy.context.evaluated_depsgraph_get()
ev = body.evaluated_get(dg)
mw = body.matrix_world
rest = body.data.vertices


def posed(p_rest):
    v = min(rest, key=lambda v: (v.co - Vector(p_rest)).length)
    return mw @ ev.data.vertices[v.index].co


upper_lip, lower_lip = posed((0, -0.1488, 1.5026)), posed((0, -0.1471, 1.5022))
mouth = (upper_lip + lower_lip) / 2
brow = posed((0, -0.15, 1.6))
hb = rig.pose.bones["head"]
delta = (rig.matrix_world.to_3x3() @ hb.matrix.to_3x3()).to_quaternion().to_matrix() @ \
    hb.bone.matrix_local.to_3x3().to_quaternion().to_matrix().inverted()
front, up, side = delta @ Vector((0, -1, 0)), delta @ Vector((0, 0, 1)), delta @ Vector((1, 0, 0))
face = (mouth + brow) / 2
print("FACE mouth", tuple(round(c, 3) for c in mouth), "front", tuple(round(c, 2) for c in front))


def look(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()


def light(name, kind, loc, target, energy, color=(1, 1, 1), size=None, spot=None):
    l = bpy.data.objects.new(name, bpy.data.lights.new(name, kind))
    scene.collection.objects.link(l)
    l.location = Vector(loc)
    look(l, target)
    l.data.energy = energy
    l.data.color = color
    if size is not None:
        if kind == "AREA":
            l.data.size = size
        else:
            l.data.shadow_soft_size = size
    if spot:
        l.data.spot_size, l.data.spot_blend = math.radians(spot[0]), spot[1]
    return l


cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
scene.collection.objects.link(cam)
scene.camera = cam
cam.data.dof.use_dof = True
cam.data.dof.aperture_fstop = 2.8

# Shared atmosphere: cold rim from behind, red emergency glow low behind the figure.
light("Rim", "AREA", (-1.4, 2.2, 3.0), (0, 0, 1.6), 420, (0.45, 0.6, 1.0), size=1.2)
light("Emergency", "POINT", (1.6, 2.6, 0.5), (0, 0, 1), 140, (1.0, 0.07, 0.03), size=0.2)

SHOTS = {
    "full": dict(cam=(0.9, -2.3, 0.5), target=(0, -0.1, 1.28), lens=24, res=(1080, 1440),
                 flash=((0.62, -2.1, 0.9), (0, -0.35, 1.8), 380, (32, 0.65))),
    "face": dict(cam=tuple(face + front * 0.62 - up * 0.16 + side * 0.2), target=tuple(face * 0.6 + mouth * 0.4),
                 lens=70, res=(1080, 1080),
                 flash=(tuple(face + front * 0.55 - up * 0.45 - side * 0.15), tuple(face), 95, (28, 0.6))),
    "side": dict(cam=(2.7, -1.1, 1.25), target=(0, -0.15, 1.45), lens=38, res=(1080, 1440),
                 flash=((2.2, -1.6, 1.3), (0, -0.2, 1.7), 1200, (36, 0.55))),
}
for s in shots:
    spec = SHOTS[s]
    for o in [o for o in scene.objects if o.name.startswith("Flash")]:
        bpy.data.objects.remove(o, do_unlink=True)
    pos, tgt, energy, spot = spec["flash"]
    light("Flash", "SPOT", pos, tgt, energy, (1.0, 0.96, 0.88), size=0.03, spot=spot)
    cam.location = Vector(spec["cam"])
    look(cam, spec["target"])
    cam.data.lens = spec["lens"]
    cam.data.dof.focus_distance = (Vector(spec["target"]) - cam.location).length
    scene.render.resolution_x, scene.render.resolution_y = spec["res"]
    scene.render.filepath = f"{out}_{s}.png"
    t0 = time.time()
    bpy.ops.render.render(write_still=True)
    print("RENDERED", s, "%.0fs" % (time.time() - t0))
