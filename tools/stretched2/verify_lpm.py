# Round-trip check for the .lpm export: skin every exported vertex into the idle pose using only the
# file's own matrices (the same math Unity's SkinnedMeshRenderer does), convert back to Blender space,
# and measure the distance to Blender's evaluated idle mesh.
# Usage: blender -b 06_game.blend -P verify_lpm.py -- <work_dir> <file.lpm>
import bpy, json, os, struct, sys
from mathutils import Matrix, Vector, kdtree

work, path = sys.argv[sys.argv.index("--") + 1:][:2]
data = open(path, "rb").read()
off = 0


def rd(fmt):
    global off
    v = struct.unpack_from("<" + fmt, data, off)
    off += struct.calcsize("<" + fmt)
    return v


def rstr():
    (n,) = rd("i")
    global off
    s = data[off:off + n].decode()
    off += n
    return s


assert data[:4] == b"LPM1"
off = 8
(nb,) = rd("i")
rest, idle, parents = [], [], []
for _ in range(nb):
    rstr()
    parents.append(rd("i")[0])
    rest.append(Matrix([rd("4f") for _ in range(4)]))
    idle.append(Matrix([rd("4f") for _ in range(4)]))
skin = [idle[i] @ rest[i].inverted() for i in range(nb)]
(nv,) = rd("i")
C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
lift = -json.load(open(os.path.join(work, "game_meta.json")))["idle_floor"]
posed = []
for _ in range(nv):
    p = Vector(rd("3f"))
    rd("3f"); rd("2f")
    bi, w = rd("4i"), rd("4f")
    q = sum((skin[b] @ p * wt for b, wt in zip(bi, w) if wt > 0), Vector((0, 0, 0)))
    qb = C.inverted() @ q.to_4d()
    posed.append(Vector((qb.x, qb.y, qb.z - lift)))

rig = bpy.data.objects["StretchedRig"]
rig.data.pose_position = "POSE"
dg = bpy.context.evaluated_depsgraph_get()
ref = []
for n in ("StretchedLow", "SuitLow", "TeethUpperLow", "TeethLowerLow"):
    o = bpy.data.objects[n].evaluated_get(dg)
    ref += [o.matrix_world @ v.co for v in o.data.vertices]
kd = kdtree.KDTree(len(ref))
for i, p in enumerate(ref):
    kd.insert(p, i)
kd.balance()
errs = sorted(kd.find(p)[2] for p in posed)
print("VERIFY verts", len(posed), "median err %.5f m  p99 %.5f m  max %.5f m" % (
    errs[len(errs) // 2], errs[int(len(errs) * 0.99)], errs[-1]))
