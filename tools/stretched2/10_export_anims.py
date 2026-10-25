# Step 10: bake every clip to LethalPlus's runtime animation format (.lpa), in Unity space.
#
# LPA1 layout (little endian):
#   "LPA1" int32 version
#   int32 boneCount, str[boneCount] bone names (must match the .lpm bone order)
#   int32 clipCount, per clip:
#       str name, int32 frameCount, float32 fps, int32 flags (1 = loops, 2 = lives on the ceiling)
#       per frame: float32[3] root bone local position, then per bone float32[4] local rotation (x, y, z, w)
#   str = int32 byte length + utf-8
#
# Child bones never translate (all motion is rotation), so only the root bone carries a position.
# Usage: blender -b 09_animations.blend -P 10_export_anims.py -- <work_dir> <out.lpa>
import bpy, json, math, os, struct, sys
from mathutils import Matrix, Vector

work, out_path = sys.argv[sys.argv.index("--") + 1:][:2]
scene = bpy.context.scene
rig = bpy.data.objects["StretchedRig"]
lift = -json.load(open(os.path.join(work, "game_meta.json")))["rest_floor"]
C3 = Matrix(((-1, 0, 0), (0, 0, 1), (0, -1, 0)))
C3i = C3.inverted()

for o in scene.objects:  # only the armature needs evaluating
    if o is not rig:
        o.hide_viewport = True
rig.location, rig.rotation_euler = (0, 0, 0), (0, 0, 0)
bones = list(rig.data.bones)
roots = [i for i, b in enumerate(bones) if b.parent is None]
assert len(roots) == 1, roots
root = bones[roots[0]]

# Rest local offsets in Unity space, for the FK self-check below.
rest_local_t = {}
for b in bones:
    rel = (b.parent.matrix_local.inverted() @ b.matrix_local) if b.parent else b.matrix_local.copy()
    t = rel.translation.copy()
    if not b.parent:
        t.z += lift
    rest_local_t[b.name] = C3 @ t

clips = sorted((a for a in bpy.data.actions if "lp_length" in a), key=lambda a: a.name)
buf = bytearray()
w_i32 = lambda v: buf.extend(struct.pack("<i", v))
w_f32s = lambda vs: buf.extend(struct.pack(f"<{len(vs)}f", *vs))


def w_str(s):
    b = s.encode("utf-8")
    w_i32(len(b))
    buf.extend(b)


buf.extend(b"LPA1")
w_i32(1)
w_i32(len(bones))
for b in bones:
    w_str(b.name)
w_i32(len(clips))
worst = 0.0
for action in clips:
    rig.animation_data.action = action
    if getattr(action, "slots", None):
        rig.animation_data.action_slot = action.slots[0]
    n = int(action["lp_length"]) + 1
    flags = (1 if action["lp_loop"] else 0) | (2 if action["lp_ceiling"] else 0)
    w_str(action.name)
    w_i32(n)
    w_f32s([float(scene.render.fps)])
    w_i32(flags)
    first_q, motion = None, 0.0
    for f in range(n):
        scene.frame_set(f)
        M = {b.name: rig.pose.bones[b.name].matrix.copy() for b in bones}
        rp = M[root.name].translation.copy()
        rp.z += lift
        rpu = C3 @ rp
        w_f32s([rpu.x, rpu.y, rpu.z])
        quats = {}
        for b in bones:
            local = (M[b.parent.name].inverted() @ M[b.name]) if b.parent else M[b.name]
            q = (C3 @ local.to_3x3().normalized() @ C3i).to_quaternion()
            quats[b.name] = q
            w_f32s([q.x, q.y, q.z, q.w])
        if first_q is None:
            first_q = quats
        else:
            motion = max(motion, max(math.degrees(first_q[k].rotation_difference(q).angle) for k, q in quats.items()))
        # FK self-check on a few frames: rebuild bone heads from the written data, compare to Blender.
        if f in (0, n // 2, n - 1):
            W = {}
            for b in bones:
                if b.parent:
                    pr, pt = W[b.parent.name]
                    W[b.name] = (pr @ quats[b.name].to_matrix(), pt + pr @ rest_local_t[b.name])
                else:
                    W[b.name] = (quats[b.name].to_matrix(), rpu)
            for b in bones:
                h = M[b.name].translation.copy()
                h.z += lift
                worst = max(worst, (W[b.name][1] - C3 @ h).length)
    print("CLIP %-18s frames %3d flags %d  max rotation from first frame %5.1f deg" % (action.name, n, flags, motion))

with open(out_path, "wb") as fh:
    fh.write(buf)
print("EXPORT clips", len(clips), "bones", len(bones), "bytes", len(buf), "FK check worst error %.6f" % worst)
