# Step 0: pull Blender Studio's CC0 realistic male base mesh into a clean working file,
# centered at the origin, and render orthographic front/side views over a 10 cm grid
# so joint positions can be read off for rigging.
# Usage: blender -b --factory-startup -P 00_prepare_base.py -- <bundle.blend> <work_dir>
import bpy, math, os, sys
from mathutils import Vector

bundle, work = sys.argv[sys.argv.index("--") + 1:][:2]
os.makedirs(work, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

with bpy.data.libraries.load(bundle, link=False) as (src, dst):
    dst.objects = [n for n in src.objects if n.startswith("GEO-body_male_realistic")]
for o in dst.objects:
    scene.collection.objects.link(o)

body = bpy.data.objects["GEO-body_male_realistic"]
offset = Vector((-body.location.x, -body.location.y, 0))
for o in dst.objects:
    if o.parent is None:
        o.location += offset
bpy.context.view_layer.update()

mr = next(m for m in body.modifiers if m.type == "MULTIRES")
print("MULTIRES total", mr.total_levels, "viewport", mr.levels, "render", mr.render_levels, "sculpt", mr.sculpt_levels)

mw = body.matrix_world
pts = [mw @ v.co for v in body.data.vertices]
lo = Vector([min(p[i] for p in pts) for i in range(3)])
hi = Vector([max(p[i] for p in pts) for i in range(3)])
print("BOUNDS", tuple(round(x, 3) for x in lo), tuple(round(x, 3) for x in hi))


def slab(zmin, zmax, xmin=-9, xmax=9):
    s = [p for p in pts if zmin <= p.z < zmax and xmin <= p.x < xmax]
    if not s:
        return None
    return (round(min(p.x for p in s), 3), round(max(p.x for p in s), 3),
            round(min(p.y for p in s), 3), round(max(p.y for p in s), 3), len(s))


# Horizontal cross-section widths every 5 cm: reveals neck, shoulders, waist, crotch, knees.
for z10 in range(0, 170, 5):
    z = z10 / 100
    print("SLAB", f"{z:.2f}", slab(z, z + 0.05))
# Right-side (x<0 is the figure's right, since it faces -Y) arm-only sections.
for z10 in range(60, 150, 5):
    z = z10 / 100
    print("ARM_R", f"{z:.2f}", slab(z, z + 0.05, -9, -0.2))

# Grid lines every 10 cm (thin boxes) + labels are implicit: read off by position.
mat = bpy.data.materials.new("Grid")
mat.use_nodes = True
mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (1, 0.1, 0.1, 1)
mat.node_tree.nodes["Principled BSDF"].inputs["Emission Color"].default_value = (1, 0.1, 0.1, 1)
mat.node_tree.nodes["Principled BSDF"].inputs["Emission Strength"].default_value = 2
grid = []
for i in range(0, 18):
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0.4, i * 0.1))
    g = bpy.context.active_object
    g.scale = (2.0, 0.001, 0.0015 if i % 5 else 0.004)
    g.data.materials.append(mat)
    grid.append(g)
for i in range(-6, 7):
    bpy.ops.mesh.primitive_cube_add(size=1, location=(i * 0.1, 0.4, 0.9))
    g = bpy.context.active_object
    g.scale = (0.0015 if i % 5 else 0.004, 0.001, 1.8)
    g.data.materials.append(mat)
    grid.append(g)

world = bpy.data.worlds.new("W")
world.color = (0.9, 0.9, 0.9)
scene.world = world
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x, scene.render.resolution_y = 900, 1100

cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
scene.collection.objects.link(cam)
scene.camera = cam
cam.data.type = "ORTHO"
cam.data.ortho_scale = 1.9

for view, loc, rot in (("front", (0, -5, 0.9), (math.radians(90), 0, 0)),
                       ("side", (5, 0, 0.9), (math.radians(90), 0, math.radians(90)))):
    cam.location, cam.rotation_euler = loc, rot
    for g in grid:  # keep grid behind the body for each view
        g.location.y = 0.4 if view == "front" else g.location.y
    if view == "side":
        for g in grid:
            g.rotation_euler = (0, 0, math.radians(90))
            g.location.x, g.location.y = -0.4, g.location.x
    scene.render.filepath = os.path.join(work, f"base_{view}.png")
    bpy.ops.render.render(write_still=True)

for g in grid:
    bpy.data.objects.remove(g, do_unlink=True)
bpy.data.materials.remove(mat)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work, "00_base.blend"))
print("PREP_OK")
