# Step 7: bake the high-poly look onto the game mesh. Both meshes are put in the same stretched rest
# shape (scale-only pose), then Cycles bakes selected-to-active: skin albedo/normal/roughness (2K) and
# suit albedo/roughness (1K). Roughness is packed into an HDRP mask map (A = smoothness).
# Usage: blender -b 04_materials.blend -P 07_bake.py -- <work_dir> <out_dir>
import bpy, os, sys
import numpy as np

work, out_dir = sys.argv[sys.argv.index("--") + 1:][:2]
os.makedirs(out_dir, exist_ok=True)
scene = bpy.context.scene
rig = bpy.data.objects["StretchedRig"]
high_body = bpy.data.objects["Stretched"]
high_suit = bpy.data.objects["SuitScraps"]

with bpy.data.libraries.load(os.path.join(work, "06_game.blend"), link=False) as (src, dst):
    dst.objects = ["BakeLowBody", "BakeLowSuit"]
low_body, low_suit = dst.objects
for o in (low_body, low_suit):
    scene.collection.objects.link(o)

# Same stretched rest shape as the game mesh: rig at the origin, scale-only pose, no smoothing.
rig.location = (0, 0, 0)
for pb in rig.pose.bones:
    pb.rotation_mode = "QUATERNION"
    pb.rotation_quaternion = (1, 0, 0, 0)
    pb.location = (0, 0, 0)
for o in (high_body, high_suit):
    for m in o.modifiers:
        if m.type == "CORRECTIVE_SMOOTH":
            m.show_render = m.show_viewport = False
for o in scene.objects:
    if o.name.startswith("Teeth"):
        o.hide_render = True
bpy.context.view_layer.update()

scene.render.engine = "CYCLES"
try:
    prefs = bpy.context.preferences.addons["cycles"].preferences
    prefs.compute_device_type = "CUDA"
    prefs.get_devices()
    for d in prefs.devices:
        d.use = True
    scene.cycles.device = "GPU"
except Exception:
    scene.cycles.device = "CPU"
bake = scene.render.bake
bake.use_selected_to_active = True
bake.cage_extrusion = 0.01
bake.max_ray_distance = 0.04
bake.margin = 16


def target_material(obj, name):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    node = m.node_tree.nodes.new("ShaderNodeTexImage")
    m.node_tree.nodes.active = node
    obj.data.materials.clear()
    obj.data.materials.append(m)
    return node


def emission_route(mat, on):
    """Bake albedo: feed whatever drives Base Color straight into an emission shader."""
    nt = mat.node_tree
    out = next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL")
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    if on:
        em = nt.nodes.new("ShaderNodeEmission")
        em.name = "__bake_emit__"
        link = bsdf.inputs["Base Color"].links
        if link:
            nt.links.new(link[0].from_socket, em.inputs["Color"])
        else:
            em.inputs["Color"].default_value = bsdf.inputs["Base Color"].default_value
        nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    else:
        nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])


def bake_maps(high, low, prefix, size, kinds):
    node = target_material(low, prefix + "_target")
    for o in scene.objects:
        o.select_set(False)
    high.select_set(True)
    low.select_set(True)
    bpy.context.view_layer.objects.active = low
    mat = high.data.materials[0]
    images = {}
    for kind in kinds:
        img = bpy.data.images.new(f"{prefix}_{kind}", size, size, alpha=False)
        if kind != "albedo":
            img.colorspace_settings.name = "Non-Color"
        node.image = img
        if kind == "albedo":
            scene.cycles.samples = 32  # the skin material has an AO term
            emission_route(mat, True)
            bpy.ops.object.bake(type="EMIT")
            emission_route(mat, False)
        elif kind == "normal":
            scene.cycles.samples = 4
            bpy.ops.object.bake(type="NORMAL", normal_space="TANGENT")
        elif kind == "rough":
            scene.cycles.samples = 4
            bpy.ops.object.bake(type="ROUGHNESS")
        images[kind] = img
        print("BAKED", prefix, kind)
    return images


def save(img, name):
    img.filepath_raw = os.path.join(out_dir, name)
    img.file_format = "PNG"
    img.save()


def save_mask(rough_img, name):
    """HDRP Lit mask map: R metallic, G occlusion, B detail mask, A smoothness."""
    w, h = rough_img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    rough_img.pixels.foreach_get(px)
    r = px.reshape(-1, 4)[:, 0]
    mask = np.zeros((w * h, 4), dtype=np.float32)
    mask[:, 1] = 1.0
    mask[:, 3] = 1.0 - r
    img = bpy.data.images.new(name, w, h, alpha=True)
    img.colorspace_settings.name = "Non-Color"
    img.pixels.foreach_set(mask.ravel())
    save(img, name + ".png")


skin = bake_maps(high_body, low_body, "StretchedSkin", 2048, ("albedo", "normal", "rough"))
save(skin["albedo"], "StretchedSkin_albedo.png")
save(skin["normal"], "StretchedSkin_normal.png")
save_mask(skin["rough"], "StretchedSkin_mask")
suit = bake_maps(high_suit, low_suit, "StretchedSuit", 1024, ("albedo", "rough"))
save(suit["albedo"], "StretchedSuit_albedo.png")
save_mask(suit["rough"], "StretchedSuit_mask")
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work, "07_bake.blend"))
print("BAKE_OK")
