# Step 8: export the game model to LethalPlus's runtime format (.lpm), already in Unity coordinates.
#
# LPM1 layout (little endian):
#   "LPM1" int32 version
#   int32 boneCount, per bone: str name, int32 parent, float32[16] rest, float32[16] idle
#       (row-major model-space matrices; rest is the bind pose, idle the hunched default pose)
#   int32 vertexCount, per vertex: float32[3] pos, float32[3] normal, float32[2] uv,
#       int32[4] bone indices, float32[4] weights
#   int32 submeshCount, per submesh: str material, int32 indexCount, int32[indexCount] triangles
#   str = int32 byte length + utf-8
#
# Blender is Z-up right-handed, Unity Y-up left-handed: p_unity = (-x, z, -y). That mapping is a
# reflection, so triangle index order is kept as-is (Blender CCW-front becomes Unity CW-front).
# Usage: blender -b 06_game.blend -P 08_export.py -- <work_dir> <out.lpm>
import bpy, json, os, struct, sys
from mathutils import Matrix, Vector

work, out_path = sys.argv[sys.argv.index("--") + 1:][:2]
rig = bpy.data.objects["StretchedRig"]
meta = json.load(open(os.path.join(work, "game_meta.json")))
lift = -meta["rest_floor"]  # feet on y = 0 in the rest pose
idle_lift = -meta["idle_floor"]

C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
C_inv = C.inverted()


def to_unity_matrix(m, dz):
    m = m.copy()
    m.translation.z += dz
    return C @ m @ C_inv


def to_unity_point(p, dz):
    return C.to_3x3() @ Vector((p.x, p.y, p.z + dz))


bones = list(rig.data.bones)
bone_index = {b.name: i for i, b in enumerate(bones)}
rig.data.pose_position = "POSE"
bpy.context.view_layer.update()
pose = rig.pose.bones

buf = bytearray()
w_i32 = lambda v: buf.extend(struct.pack("<i", v))
w_f32s = lambda vs: buf.extend(struct.pack(f"<{len(vs)}f", *vs))


def w_str(s):
    b = s.encode("utf-8")
    w_i32(len(b))
    buf.extend(b)


def w_mat(m):
    w_f32s([m[r][c] for r in range(4) for c in range(4)])


buf.extend(b"LPM1")
w_i32(1)
w_i32(len(bones))
for b in bones:
    w_str(b.name)
    w_i32(bone_index[b.parent.name] if b.parent else -1)
    w_mat(to_unity_matrix(b.matrix_local, lift))
    w_mat(to_unity_matrix(pose[b.name].matrix, idle_lift))

# Mesh: rest-pose data of every game part, split per corner so UV seams get their own vertices.
parts = [bpy.data.objects[n] for n in ("StretchedLow", "SuitLow", "TeethUpperLow", "TeethLowerLow")]
verts, index_of, submeshes = [], {}, {}
for obj in parts:
    me = obj.data
    me.calc_loop_triangles()
    uv_layer = me.uv_layers.get("UVMap") or (me.uv_layers[0] if me.uv_layers else None)
    gname = {g.index: g.name for g in obj.vertex_groups}
    normals = me.corner_normals
    mat_name = me.materials[0].name if me.materials else "Skin"
    tris = submeshes.setdefault(mat_name, [])
    for tri in me.loop_triangles:
        for li in tri.loops:
            vi = me.loops[li].vertex_index
            uv = tuple(uv_layer.data[li].uv) if uv_layer else (0.0, 0.0)
            n = normals[li].vector
            key = (obj.name, vi, round(uv[0], 5), round(uv[1], 5), round(n.x, 3), round(n.y, 3), round(n.z, 3))
            idx = index_of.get(key)
            if idx is None:
                v = me.vertices[vi]
                ws = sorted(((ge.weight, bone_index[gname[ge.group]]) for ge in v.groups
                             if gname.get(ge.group) in bone_index and ge.weight > 1e-4), reverse=True)[:4]
                tot = sum(w for w, _ in ws) or 1.0
                ws = [(w / tot, bi) for w, bi in ws] + [(0.0, 0)] * (4 - len(ws))
                if all(w == 0 for w, _ in ws):
                    ws[0] = (1.0, bone_index["spine2"])
                p = to_unity_point(v.co, lift)
                nu = C.to_3x3() @ n
                idx = len(verts)
                index_of[key] = idx
                verts.append((p, nu, uv, ws))
            tris.append(idx)

w_i32(len(verts))
for p, n, uv, ws in verts:
    w_f32s([p.x, p.y, p.z, n.x, n.y, n.z, uv[0], uv[1]])
    buf.extend(struct.pack("<4i", *[bi for _, bi in ws]))
    w_f32s([w for w, _ in ws])
w_i32(len(submeshes))
for name, tris in submeshes.items():
    w_str(name)
    w_i32(len(tris))
    buf.extend(struct.pack(f"<{len(tris)}i", *tris))

with open(out_path, "wb") as fh:
    fh.write(buf)
ys = [p.y for p, _, _, _ in verts]
print("EXPORT bones", len(bones), "verts", len(verts), "tris", sum(len(t) for t in submeshes.values()) // 3,
      "submeshes", {k: len(v) // 3 for k, v in submeshes.items()}, "height %.3f..%.3f" % (min(ys), max(ys)),
      "bytes", len(buf))
