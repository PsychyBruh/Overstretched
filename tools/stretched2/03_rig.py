# Step 3: skeleton, weights, teeth and the Stretched pose.
# Usage: blender -b 02_sculpt.blend -P 03_rig.py -- <work_dir>
import bpy, bmesh, json, math, os, random, sys
from mathutils import Matrix, Vector

work = sys.argv[sys.argv.index("--") + 1]
scene = bpy.context.scene
body = bpy.data.objects["Stretched"]
proxy = bpy.data.objects["Proxy"]
scraps = bpy.data.objects["SuitScraps"]
cut = json.load(open(os.path.join(work, "cut.json")))
rnd = random.Random(7)


def select_only(o):
    for x in scene.objects:
        x.select_set(False)
    o.select_set(True)
    bpy.context.view_layer.objects.active = o


def smoothstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


# ---------------------------------------------------------------- landmarks (figure faces -Y, left = +X)
P = [v.co.copy() for v in body.data.vertices]


def centroid(cond):
    s = [p for p in P if cond(p)]
    return sum(s, Vector()) / len(s)


TOP = max(p.z for p in P)
L = {}
L["knee"] = centroid(lambda p: p.x < -0.02 and 0.46 < p.z < 0.48)
L["ankle"] = centroid(lambda p: p.x < -0.02 and 0.085 < p.z < 0.105)
foot = [p for p in P if p.x < -0.02 and p.z < 0.04]
ymin = min(p.y for p in foot)
L["toe"] = centroid(lambda p: p.x < -0.02 and p.z < 0.04 and p.y < ymin + 0.012)
def yc(z, w, ymin=-9.0):
    """Depth centre of a horizontal slice (bounding box, so dense pecs/face don't bias it)."""
    s = [p.y for p in P if abs(p.z - z) < 0.01 and abs(p.x) < w and p.y > ymin]
    return (min(s) + max(s)) / 2
L["hip"] = Vector((-0.09, yc(0.885, 0.15), 0.885))
L["pelvis"] = Vector((0, yc(0.95, 0.15), 0.95))
L["spine1"] = Vector((0, yc(1.06, 0.12), 1.06))
L["spine2"] = Vector((0, yc(1.22, 0.12), 1.22))
L["neck0"] = Vector((0, yc(1.425, 0.06, -0.09), 1.425))
L["neck1"] = Vector((0, yc(1.46, 0.05, -0.085), 1.46))
L["neck2"] = Vector((0, yc(1.495, 0.05, -0.08), 1.495))
L["skull"] = Vector((0, -0.03, 1.535))
L["crown"] = Vector((0, -0.03, TOP))
L["hinge"] = Vector((0, -0.028, 1.528))
L["chin"] = centroid(lambda p: abs(p.x) < 0.01 and p.y < -0.1 and 1.44 < p.z < 1.47)
L["shoulder"] = centroid(lambda p: p.x < -0.155 and 1.36 < p.z < 1.39)
L["elbow"] = centroid(lambda p: p.x < -0.2 and 1.11 < p.z < 1.13)
L["wrist"] = centroid(lambda p: p.x < -0.27 and 0.86 < p.z < 0.88)
arm = [p for p in P if p.x < -0.3]
zmin = min(p.z for p in arm)
L["tip"] = centroid(lambda p: p.x < -0.3 and p.z < zmin + 0.015)
for k, v in L.items():
    print("LM", k, tuple(round(c, 3) for c in v))
mirror = lambda v: Vector((-v.x, v.y, v.z))

# ---------------------------------------------------------------- armature
bpy.ops.object.armature_add(location=(0, 0, 0))
rig = bpy.context.active_object
rig.name = "StretchedRig"
bpy.ops.object.mode_set(mode="EDIT")
eb = rig.data.edit_bones
eb.remove(eb[0])


def bone(name, head, tail, parent=None, connect=True):
    b = eb.new(name)
    b.head, b.tail = Vector(head), Vector(tail)
    b.roll = 0
    if parent:
        b.parent = eb[parent]
        b.use_connect = connect and (b.head - eb[parent].tail).length < 1e-5
    b.inherit_scale = "NONE"
    return b


bone("pelvis", L["pelvis"], L["spine1"])
bone("spine1", L["spine1"], L["spine2"], "pelvis")
bone("spine2", L["spine2"], L["neck0"] - Vector((0, 0, 0.1)), "spine1")
bone("spine3", L["neck0"] - Vector((0, 0, 0.1)), L["neck0"], "spine2")
bone("neck1", L["neck0"], L["neck1"], "spine3")
bone("neck2", L["neck1"], L["neck2"], "neck1")
bone("neck3", L["neck2"], L["skull"], "neck2")
bone("head", L["skull"], L["crown"], "neck3")
bone("jaw", L["hinge"], L["chin"], "head", connect=False)
for side, f in (("R", lambda v: v), ("L", mirror)):
    sh, el, wr, tip = f(L["shoulder"]), f(L["elbow"]), f(L["wrist"]), f(L["tip"])
    clav_head = Vector((math.copysign(0.02, sh.x), L["neck0"].y - 0.02, L["neck0"].z - 0.015))
    bone(f"clavicle.{side}", clav_head, sh, "spine3", connect=False)
    bone(f"upperarm.{side}", sh, el, f"clavicle.{side}")
    mid = el.lerp(wr, 0.5)
    bone(f"forearm_a.{side}", el, mid, f"upperarm.{side}")
    bone(f"forearm_b.{side}", mid, wr, f"forearm_a.{side}")
    bone(f"hand.{side}", wr, tip, f"forearm_b.{side}")
    hip, knee, ank, toe = f(L["hip"]), f(L["knee"]), f(L["ankle"]), f(L["toe"])
    bone(f"thigh.{side}", hip, knee, "pelvis", connect=False)
    bone(f"shin.{side}", knee, ank, f"thigh.{side}")
    bone(f"foot.{side}", ank, toe, f"shin.{side}")
bpy.ops.object.mode_set(mode="OBJECT")

# ---------------------------------------------------------------- weights: bone heat on proxy, transfer to detail
select_only(proxy)
rig.select_set(True)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.parent_set(type="ARMATURE_AUTO")
empty = [g.name for g in proxy.vertex_groups
         if not any(g.index in [x.group for x in v.groups] for v in proxy.data.vertices)]
print("HEAT groups", len(proxy.vertex_groups), "empty", empty)

for target in (body, scraps):
    select_only(target)
    dt = target.modifiers.new("WeightsFromProxy", "DATA_TRANSFER")
    dt.object = proxy
    dt.use_vert_data = True
    dt.data_types_verts = {"VGROUP_WEIGHTS"}
    dt.vert_mapping = "POLYINTERP_NEAREST"
    dt.layers_vgroup_select_src = "ALL"
    dt.layers_vgroup_select_dst = "NAME"
    bpy.ops.object.datalayout_transfer(modifier=dt.name)
    bpy.ops.object.modifier_apply(modifier=dt.name)

# Jaw: hard split exactly along the lips and the cheek tears, softening deeper in the mouth bag,
# behind the jaw angle and down the throat.
cutp = cut["cut"]
X_END = max(abs(c[0]) for c in cutp)


def along_cut(x, k):
    x = max(cutp[0][0], min(cutp[-1][0], x))
    for a, b in zip(cutp, cutp[1:]):
        if a[0] <= x <= b[0]:
            t = 0 if b[0] == a[0] else (x - a[0]) / (b[0] - a[0])
            return a[k] + (b[k] - a[k]) * t
    return cutp[-1][k]


gidx = {g.name: g.index for g in body.vertex_groups}
jaw_g = body.vertex_groups.get("jaw") or body.vertex_groups.new(name="jaw")
# Throw away bone heat's own jaw weights (they ignore the lip slit) and renormalise the rest;
# the jaw is painted explicitly below.
for v in body.data.vertices:
    wj = next((ge.weight for ge in v.groups if ge.group == jaw_g.index), 0.0)
    if wj <= 0:
        continue
    jaw_g.remove([v.index])
    others = [ge for ge in v.groups if ge.group != jaw_g.index]
    tot = sum(ge.weight for ge in others)
    if tot > 1e-6:
        for ge in others:
            ge.weight /= tot
    else:
        body.vertex_groups["head"].add([v.index], 1.0, "REPLACE")
tear_up_i, tear_lo_i = gidx["TearUpper"], gidx["TearLower"]
MOUTH_X = max(abs(p[0]) for p in cut["upper_lip"])
jaw_w = {}
for v in body.data.vertices:
    p = v.co
    if p.z < 1.38 or abs(p.x) > 0.095 or p.y > 0.01:
        continue
    zc, ycut = along_cut(p.x, 1), along_cut(p.x, 2)
    depth = p.y - (ycut + 0.012)
    groups = {ge.group for ge in v.groups}
    if tear_lo_i in groups:
        w_split = 1.0
    elif tear_up_i in groups:
        w_split = 0.0
    elif depth < 0 and abs(p.x) <= MOUTH_X + 0.001 and abs(p.z - zc) < 0.0025:
        # At the lips the two edges are a fraction of a millimetre apart: the upper lip's edge
        # faces down, the lower lip's faces up.
        w_split = 1.0 if v.normal.z > 0 else 0.0
    else:
        width = 0.0004 if depth < 0 else min(0.012, 0.0004 + depth * 0.3)
        width = max(width, 0.012 * smoothstep(X_END - 0.008, X_END + 0.004, abs(p.x)))
        w_split = smoothstep(zc + width, zc - width, p.z)
    w_side = smoothstep(-0.005, -0.035, p.y)
    t = smoothstep(-0.1, -0.02, p.y)
    w_low = smoothstep(1.35 + 0.07 * t, 1.445 + 0.025 * t, p.z)  # long falloff: throat stretches evenly
    w = w_split * w_side * w_low
    if w > 0.001:
        jaw_w[v.index] = w
for vi, w in jaw_w.items():
    v = body.data.vertices[vi]
    for ge in v.groups:
        if body.vertex_groups[ge.group].name != "jaw":
            ge.weight *= (1 - w)
    jaw_g.add([vi], w, "REPLACE")
print("JAW weighted verts", len(jaw_w))

# Lock the skull and face to the head bone: bone heat leaks neck influence into the face, and with
# a 2.5x stretched neck that drags the lips away from the (rigid) teeth.
head_i, jaw_i = body.vertex_groups["head"].index, jaw_g.index
head_g = body.vertex_groups["head"]
locked = 0
for v in body.data.vertices:
    p = v.co
    if p.z < 1.44:
        continue
    z0 = 1.455 + 0.085 * smoothstep(-0.06, 0.04, p.y)  # face reaches lower than the back of the skull
    f = smoothstep(z0, z0 + 0.035, p.z)
    if f <= 0:
        continue
    jaw_wt = next((ge.weight for ge in v.groups if ge.group == jaw_i), 0.0)
    rest = 1.0 - jaw_wt
    head_wt = next((ge.weight for ge in v.groups if ge.group == head_i), 0.0)
    for ge in v.groups:
        if ge.group not in (head_i, jaw_i):
            ge.weight *= (1 - f)
    head_g.add([v.index], head_wt * (1 - f) + f * rest, "REPLACE")
    locked += 1
print("HEAD locked verts", locked)

# Sharpen the split forearm so the second elbow reads as a joint, not a rubbery bend.
for side in ("L", "R"):
    a, b = body.vertex_groups[f"forearm_a.{side}"], body.vertex_groups[f"forearm_b.{side}"]
    for v in body.data.vertices:
        wa = wb = 0.0
        for ge in v.groups:
            if ge.group == a.index:
                wa = ge.weight
            elif ge.group == b.index:
                wb = ge.weight
        if wa > 0 and wb > 0:
            tot = wa + wb
            sa, sb = wa ** 3, wb ** 3
            a.add([v.index], tot * sa / (sa + sb), "REPLACE")
            b.add([v.index], tot * sb / (sa + sb), "REPLACE")

# ---------------------------------------------------------------- needle teeth
def resample(points, step):
    pts = sorted((Vector(p) for p in points), key=lambda p: p.x)
    out, acc = [pts[0]], 0.0
    for a, b in zip(pts, pts[1:]):
        seg = (b - a).length
        acc += seg
        if acc >= step:
            out.append(b)
            acc = 0.0
    return out


def teeth_object(name, rows, group):
    bm = bmesh.new()
    for pts, down, scale in rows:
        for p in pts:
            centre_dist = abs(p.x)
            length = scale * (0.012 + 0.008 * rnd.random()) * (1.15 if centre_dist < 0.03 else 0.8)
            radius = 0.0012 + 0.0006 * rnd.random()
            inward = Vector((0, 0.003, 0.0015 if down else -0.0015))
            base = p + inward
            direction = Vector((rnd.uniform(-0.15, 0.15), rnd.uniform(0.15, 0.35), -1 if down else 1)).normalized()
            q = Vector((0, 0, 1)).rotation_difference(direction)
            ring = [bm.verts.new(base + q @ Vector((math.cos(a) * radius, math.sin(a) * radius, 0)))
                    for a in [i * 2 * math.pi / 5 for i in range(5)]]
            tip = bm.verts.new(base + direction * length)
            for i in range(5):
                bm.faces.new((ring[i], ring[(i + 1) % 5], tip))
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    obj = bpy.data.objects.new(name, me)
    scene.collection.objects.link(obj)
    g = obj.vertex_groups.new(name=group)
    g.add(list(range(len(me.vertices))), 1.0, "REPLACE")
    for poly in me.polygons:
        poly.use_smooth = True
    return obj


upper_rows = [(resample(cut["upper_lip"], 0.0032), True, 1.0), (resample(cut["tear_upper"], 0.0036), True, 0.6)]
lower_rows = [(resample(cut["lower_lip"], 0.0034), False, 0.9), (resample(cut["tear_lower"], 0.0038), False, 0.55)]
teeth_up = teeth_object("TeethUpper", upper_rows, "head")
teeth_lo = teeth_object("TeethLower", lower_rows, "jaw")
print("TEETH", len(teeth_up.data.polygons) // 5, len(teeth_lo.data.polygons) // 5)

for o in (body, scraps, teeth_up, teeth_lo):
    o.parent = rig
    o.matrix_parent_inverse = rig.matrix_world.inverted()
    mod = next((m for m in o.modifiers if m.type == "ARMATURE"), None) or o.modifiers.new("Armature", "ARMATURE")
    mod.object = rig

# Corrective Smooth relaxes pinching at the stretched shoulders, armpits and elbows without
# blurring sculpted detail. The head is masked out so the lip split stays razor sharp.
for o in (body, scraps):
    g = o.vertex_groups.new(name="DeformSmooth")
    for v in o.data.vertices:
        g.add([v.index], 1.0 - smoothstep(1.36, 1.42, v.co.z), "REPLACE")
    cs = o.modifiers.new("CorrectiveSmooth", "CORRECTIVE_SMOOTH")
    cs.factor = 0.6
    cs.iterations = 12
    cs.smooth_type = "LENGTH_WEIGHTED"
    cs.rest_source = "ORCO"
    cs.vertex_group = g.name
    cs.use_pin_boundary = True
bpy.data.objects.remove(proxy, do_unlink=True)

# ---------------------------------------------------------------- the Stretched pose
POSE = [
    # bone, direction in armature space (None = follow parent), length, girth, twist (deg)
    ("pelvis", (0, 0.06, 1), 1.15, 0.9, 0),
    ("spine1", (0, -0.12, 1), 1.4, 0.84, 0),
    ("spine2", (0, -0.38, 1), 1.35, 0.9, 0),
    ("spine3", (0, -0.55, 1), 1.15, 0.94, 0),
    ("neck1", (0, -1, 0.5), 2.7, 0.78, 0),
    ("neck2", (0.05, -0.85, 0.75), 2.6, 0.74, 0),
    ("neck3", (0.14, -0.4, 1), 2.5, 0.74, 0),
    ("head", (0.55, -0.35, 0.75), 1.18, 0.92, "aim"),
    ("jaw", "open", 1.12, 1.0, 0),
    ("clavicle.R", (-1, -0.25, 0.28), 1.05, 1.0, 0),
    ("clavicle.L", (1, -0.32, 0.24), 1.05, 1.0, 0),
    ("upperarm.R", (-0.14, -0.25, -1), 1.75, 0.8, 0),
    ("upperarm.L", (0.24, -0.55, -1), 1.75, 0.8, 0),
    ("forearm_a.R", (-0.07, -0.55, -1), 1.75, 0.74, 0),
    ("forearm_a.L", (0.16, -0.95, -0.7), 1.75, 0.74, 0),
    ("forearm_b.R", (-0.03, 0.62, -1), 1.75, 0.7, 0),
    ("forearm_b.L", (0.1, -0.3, -1), 1.75, 0.7, 0),
    ("hand.R", (-0.02, -0.2, -1), 2.35, 0.85, 0),
    ("hand.L", (0.08, -0.65, -1), 2.35, 0.85, 0),
    ("thigh.R", (-0.06, -0.12, -1), 1.7, 0.72, 0),
    ("thigh.L", (0.08, -0.08, -1), 1.7, 0.72, 0),
    ("shin.R", (-0.03, 0.17, -1), 1.75, 0.66, 0),
    ("shin.L", (0.04, 0.14, -1), 1.75, 0.66, 0),
    ("foot.R", "rest", 1.25, 0.85, 0),
    ("foot.L", "rest", 1.25, 0.85, 0),
]
JAW_OPEN = math.radians(46)
# The hero camera (armature space, before the rig is lifted onto the floor): the head turns so the
# face looks straight down the lens.
AIM_AT = Vector((0.9, -2.3, -0.1))
pbones = rig.pose.bones
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode="POSE")
for name, direction, length, girth, twist in POSE:
    bpy.context.view_layer.update()
    pb = pbones[name]
    rest = pb.bone.matrix_local.to_3x3()
    rest_dir = rest.col[1].normalized()
    if direction == "rest":
        rot = rest.copy()
    elif direction == "open":
        par = pb.parent
        delta = par.matrix.to_quaternion().to_matrix() @ par.bone.matrix_local.to_3x3().to_quaternion().to_matrix().inverted()
        axis = (par.matrix.to_quaternion().to_matrix().col[0]).normalized()
        rot = Matrix.Rotation(JAW_OPEN, 3, axis) @ delta @ rest
    else:
        rot = rest_dir.rotation_difference(Vector(direction).normalized()).to_matrix() @ rest
    if twist == "aim":
        # Spin the head about its own (broken, tilted) axis until the face points at the camera.
        to_cam = (AIM_AT - pb.head).normalized()
        rest_front = rest @ (rest.inverted() @ Vector((0, -1, 0)))
        best = max(range(-180, 180, 2), key=lambda a: (Matrix.Rotation(math.radians(a), 3, rot.col[1].normalized())
                                                        @ rot @ rest.inverted() @ Vector((0, -1, 0))).dot(to_cam))
        rot = Matrix.Rotation(math.radians(best), 3, rot.col[1].normalized()) @ rot
        print("AIM head twist", best)
    elif twist:
        rot = Matrix.Rotation(math.radians(twist), 3, rot.col[1].normalized()) @ rot
    m = (rot @ Matrix.Diagonal((girth, length, girth))).to_4x4()
    m.translation = pb.head
    pb.matrix = m
bpy.ops.object.mode_set(mode="OBJECT")
bpy.context.view_layer.update()

dg = bpy.context.evaluated_depsgraph_get()
ev = body.evaluated_get(dg)
pts = [ev.matrix_world @ v.co for v in ev.data.vertices]
lo, hi = min(p.z for p in pts), max(p.z for p in pts)
rig.location.z -= lo
print("POSED height %.3f (feet lifted %.3f)" % (hi - lo, -lo))
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work, "03_posed.blend"))
print("RIG_OK")
