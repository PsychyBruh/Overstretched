# Step 1: find mesh openings (eye sockets, mouth) and render lit close-ups of the base head.
# Usage: blender -b 00_base.blend -P 01_inspect_face.py -- <work_dir>
import bpy, bmesh, math, os, sys
from mathutils import Vector

work = sys.argv[sys.argv.index("--") + 1]
scene = bpy.context.scene
body = bpy.data.objects["GEO-body_male_realistic"]

bm = bmesh.new()
bm.from_mesh(body.data)
boundary = [e for e in bm.edges if e.is_boundary]
# Group boundary edges into connected loops.
seen, loops = set(), []
for e in boundary:
    if e in seen:
        continue
    stack, loop = [e], []
    while stack:
        cur = stack.pop()
        if cur in seen:
            continue
        seen.add(cur)
        loop.append(cur)
        for v in cur.verts:
            stack.extend(x for x in v.link_edges if x.is_boundary and x not in seen)
    loops.append(loop)
mw = body.matrix_world
for loop in loops:
    vs = {v for e in loop for v in e.verts}
    pts = [mw @ v.co for v in vs]
    c = sum(pts, Vector()) / len(pts)
    size = Vector([max(p[i] for p in pts) - min(p[i] for p in pts) for i in range(3)])
    print("OPENING verts", len(vs), "center", tuple(round(x, 3) for x in c), "size", tuple(round(x, 3) for x in size))
print("NONMANIFOLD", sum(1 for e in bm.edges if not e.is_manifold), "total edges", len(bm.edges))
bm.free()

for o in bpy.data.objects:
    if o.name.startswith("GEO-body_male_realistic.eye"):
        print("EYE", o.name, tuple(round(x, 3) for x in o.matrix_world.translation), tuple(round(x, 3) for x in o.dimensions))

mat = bpy.data.materials.new("Clay")
mat.use_nodes = True
b = mat.node_tree.nodes["Principled BSDF"]
b.inputs["Base Color"].default_value = (0.6, 0.55, 0.5, 1)
b.inputs["Roughness"].default_value = 0.55
body.data.materials.clear()
body.data.materials.append(mat)

world = bpy.data.worlds.new("W")
world.color = (0.05, 0.05, 0.06)
scene.world = world
key = bpy.data.objects.new("Key", bpy.data.lights.new("Key", "AREA"))
key.data.energy = 60
key.data.size = 0.6
key.location = (-0.6, -1.0, 1.9)
scene.collection.objects.link(key)
fill = bpy.data.objects.new("Fill", bpy.data.lights.new("Fill", "AREA"))
fill.data.energy = 20
fill.data.size = 1.0
fill.location = (0.8, -0.8, 1.5)
scene.collection.objects.link(fill)
for l in (key, fill):
    l.rotation_euler = (Vector((0, -0.05, 1.55)) - l.location).to_track_quat("-Z", "Y").to_euler()

cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
scene.collection.objects.link(cam)
scene.camera = cam
cam.data.lens = 85
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = scene.render.resolution_y = 700
for name, loc in (("face_front", (0, -0.75, 1.56)), ("face_34", (0.45, -0.6, 1.58)), ("face_side", (0.75, -0.05, 1.56))):
    cam.location = loc
    cam.rotation_euler = (Vector((0, -0.06, 1.55)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = os.path.join(work, name + ".png")
    bpy.ops.render.render(write_still=True)
print("INSPECT_OK")
