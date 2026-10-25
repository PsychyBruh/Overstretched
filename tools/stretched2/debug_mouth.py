# Debug: cast rays from in front of the face into the open mouth and report what they hit
# (rest-pose position of the nearest vertex + its mask values).
import bpy, bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree

body = bpy.data.objects["Stretched"]
rig = bpy.data.objects["StretchedRig"]
dg = bpy.context.evaluated_depsgraph_get()
ev = body.evaluated_get(dg)
me = ev.to_mesh()
bm = bmesh.new()
bm.from_mesh(me)
bm.transform(body.matrix_world)
tree = BVHTree.FromBMesh(bm)
bm.faces.ensure_lookup_table()
rest = body.data.vertices
attr = body.data.color_attributes.get("Masks")


def posed_of(p):
    v = min(rest, key=lambda v: (v.co - Vector(p)).length)
    return v.index, Vector(bm.verts[v.index].co) if False else (body.matrix_world @ me.vertices[v.index].co)


_, up = posed_of((0, -0.1488, 1.5026))
_, lo = posed_of((0, -0.1471, 1.5022))
_, brow = posed_of((0, -0.15, 1.6))
mouth = (up + lo) / 2
hb = rig.pose.bones["head"]
delta = (rig.matrix_world.to_3x3() @ hb.matrix.to_3x3()).to_quaternion().to_matrix() @ \
    hb.bone.matrix_local.to_3x3().to_quaternion().to_matrix().inverted()
front, upv, side = delta @ Vector((0, -1, 0)), delta @ Vector((0, 0, 1)), delta @ Vector((1, 0, 0))
origin_base = mouth + front * 0.5
for du in (-0.02, -0.01, 0.0, 0.01, 0.02):
    for dv in (-0.03, -0.015, 0.0, 0.015, 0.03):
        target = mouth + side * du + (up - lo).normalized() * dv
        d = (target - origin_base).normalized()
        hit, normal, fi, dist = tree.ray_cast(origin_base, d)
        if hit is None:
            print("RAY", du, dv, "miss")
            continue
        f = bm.faces[fi]
        vi = min(f.verts, key=lambda v: (v.co - hit).length).index
        rp = rest[vi].co
        m = attr.data[vi].color if attr else (0, 0, 0, 0)
        print("RAY du %+.3f dv %+.3f -> rest (%.4f %.4f %.4f) n (%.2f %.2f %.2f) mask g %.2f in %.2f" % (
            du, dv, rp.x, rp.y, rp.z, rest[vi].normal.x, rest[vi].normal.y, rest[vi].normal.z, m[1], m[3]))
