# Measure walkable floor-to-ceiling clearance inside a level tile: shoot vertical rays on a grid and
# find gaps between an upward-facing surface (floor) and the next downward-facing surface (ceiling).
import bpy, sys
from mathutils import Vector
from mathutils.bvhtree import BVHTree
import bmesh

dg = bpy.context.evaluated_depsgraph_get()
bm = bmesh.new()
for o in bpy.data.objects:
    if o.type == "MESH":
        e = o.evaluated_get(dg)
        me = e.to_mesh()
        tmp = bmesh.new()
        tmp.from_mesh(me)
        tmp.transform(e.matrix_world)
        me2 = bpy.data.meshes.new("t")
        tmp.to_mesh(me2)
        bm.from_mesh(me2)
        tmp.free()
tree = BVHTree.FromBMesh(bm)
pts = [v.co for v in bm.verts]
lo = Vector([min(p[i] for p in pts) for i in range(3)])
hi = Vector([max(p[i] for p in pts) for i in range(3)])
gaps = []
N = 40
for ix in range(N):
    for iy in range(N):
        x = lo.x + (ix + 0.5) / N * (hi.x - lo.x)
        y = lo.y + (iy + 0.5) / N * (hi.y - lo.y)
        hits, z = [], hi.z + 1
        while True:
            loc, nrm, idx, dist = tree.ray_cast(Vector((x, y, z)), Vector((0, 0, -1)))
            if loc is None:
                break
            hits.append((loc.z, nrm.z))
            z = loc.z - 0.001
        hits.sort()
        for (z1, n1), (z2, n2) in zip(hits, hits[1:]):
            if n1 > 0.7 and n2 < -0.7 and 1.5 < z2 - z1 < 12:
                gaps.append(z2 - z1)
gaps.sort()
name = bpy.path.basename(bpy.data.filepath)[:-6]
if gaps:
    q = lambda f: gaps[min(len(gaps) - 1, int(f * len(gaps)))]
    print("CLEAR %-28s samples %4d  min %.2f  p10 %.2f  median %.2f  max %.2f" % (name, len(gaps), gaps[0], q(0.1), q(0.5), gaps[-1]))
else:
    print("CLEAR %-28s no enclosed gaps found" % name)
