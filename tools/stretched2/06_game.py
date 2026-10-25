# Step 6: the game model. Low-poly body = the base mesh's own clean topology (10.5k verts, real UVs)
# with the sculpt copied over by vertex index; cheek tears re-cut; bone-heat weights + exact jaw/skull
# painting; decimated suit scraps; teeth. The stretched proportions are baked into the rest pose so the
# game only ever rotates bones; the hunched idle pose is stored separately.
# Usage: blender -b 03_posed.blend -P 06_game.py -- <work_dir>
import bpy, bmesh, heapq, json, math, os, sys
from mathutils import Matrix, Vector

work = sys.argv[sys.argv.index("--") + 1]
scene = bpy.context.scene
rig = bpy.data.objects["StretchedRig"]
dense = bpy.data.objects["Stretched"]
scraps_hi = bpy.data.objects["SuitScraps"]


def select_only(*objs):
    for x in scene.objects:
        x.select_set(False)
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]


def smoothstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


EYE_REACH = 0.024  # radius of sealed-lid skin that follows each eye bone


# The render rig was lifted so the posed feet touched the floor; the game rig lives at the origin.
rig.location = (0, 0, 0)
# Capture the hero pose before anything changes: posed armature-space rotation and stretch per bone.
bpy.context.view_layer.update()
POSED_ROT = {pb.name: pb.matrix.to_3x3().normalized() for pb in rig.pose.bones}
STRETCH = {pb.name: pb.scale.copy() for pb in rig.pose.bones}

# ---------------------------------------------------------------- low-poly body
with bpy.data.libraries.load(os.path.join(work, "02_sculpt.blend"), link=False) as (src, dst):
    dst.objects = ["Proxy"]
low = dst.objects[0]
scene.collection.objects.link(low)
low.name = "StretchedLow"
low.parent = None
low.matrix_world = Matrix.Identity(4)
low.modifiers.clear()
low.vertex_groups.clear()
for i, v in enumerate(low.data.vertices):  # first 10.5k dense verts are the base verts, sculpted
    v.co = dense.data.vertices[i].co
print("LOW verts", len(low.data.vertices), "faces", len(low.data.polygons))

# Cheek tears: walk the lip edge loops to the mouth corners, then cut back toward each ear.
bm = bmesh.new()
bm.from_mesh(low.data)
bm.normal_update()


def lip_loop(sign):
    zone = [v for v in bm.verts if abs(v.co.x) < 0.001 and 1.495 < v.co.z < 1.51 and v.co.y < -0.14]
    a = next(v for v in zone if all((e.other_vert(v).co.z - v.co.z) * sign >= -1e-5 for e in v.link_edges))
    out = [a]
    for n in sorted((e.other_vert(a) for e in a.link_edges), key=lambda u: -abs(u.co.x - a.co.x))[:2]:
        prev, cur = a, n
        while True:
            out.append(cur)
            if len(cur.link_edges) != 4 or len(out) > 80:
                break
            ep = bm.edges.get((prev, cur))
            nxt = [e for e in cur.link_edges if e is not ep and not (set(e.link_faces) & set(ep.link_faces))]
            if len(nxt) != 1:
                break
            prev, cur = cur, nxt[0].other_vert(cur)
    return sorted(out, key=lambda v: v.co.x)


def dijkstra(src, goal, allowed):
    dist, prev, heap = {src: 0.0}, {}, [(0.0, src.index, src)]
    while heap:
        d, _, v = heapq.heappop(heap)
        if v is goal:
            path = [v]
            while path[-1] in prev:
                path.append(prev[path[-1]])
            return path[::-1]
        if d > dist.get(v, 1e9):
            continue
        for e in v.link_edges:
            u = e.other_vert(v)
            if allowed(u):
                nd = d + e.calc_length()
                if nd < dist.get(u, 1e9):
                    dist[u], prev[u] = nd, v
                    heapq.heappush(heap, (nd, u.index, u))


upper_lip, lower_lip = lip_loop(1), lip_loop(-1)
upper_pts = [v.co.copy() for v in upper_lip]
lower_pts = [v.co.copy() for v in lower_lip]
face_region = lambda u: u.co.y < -0.02 and 1.44 < u.co.z < 1.56 and abs(u.co.x) < 0.085
cut_edges, tear = [], {}
for side in (1, -1):
    corner = max(upper_lip, key=lambda v: v.co.x * side)
    aim = Vector((side * 0.068, -0.06, 1.505))
    goal = min((u for u in bm.verts if face_region(u) and u.normal.x * side > 0.3), key=lambda u: (u.co - aim).length)
    seg = dijkstra(corner, goal, face_region)
    tear[side] = [v.co.copy() for v in seg]
    cut_edges += [bm.edges.get((a, b)) for a, b in zip(seg, seg[1:])]
mid = [((u.x + l.x) / 2, (u.z + l.z) / 2, (u.y + l.y) / 2) for u, l in
       zip(upper_pts, [min(lower_pts, key=lambda q, u=u: abs(q.x - u.x)) for u in upper_pts])]
cutp = sorted(mid + [(p.x, p.z, p.y) for s in (1, -1) for p in tear[s]])
bmesh.ops.split_edges(bm, edges=[e for e in cut_edges if e])
bm.normal_update()
tear_up, tear_lo = [], []
for v in bm.verts:
    if v.is_boundary:
        fc = sum((f.calc_center_median() for f in v.link_faces), Vector()) / max(1, len(v.link_faces))
        (tear_up if fc.z > v.co.z else tear_lo).append(v.index)
bm.to_mesh(low.data)
bm.free()
print("LOW tear edges", len(cut_edges), "upper", len(tear_up), "lower", len(tear_lo))

# ---------------------------------------------------------------- weights
rig.data.pose_position = "REST"
bpy.context.view_layer.update()
# Bone heat fails on this mesh (the sealed eyelids collapse faces), but the render mesh's first 10.5k
# verts ARE these verts, already weighted on the clean base mesh. Copy them by index (duplicates from
# the tear cut copy their twin), minus the jaw, which is repainted below for this mesh's own cut.
vg = low.vertex_groups
bone_names = [b.name for b in rig.data.bones]
for n in bone_names:
    vg.new(name=n)
dname = {g.index: g.name for g in dense.vertex_groups}
NBASE = 10582
dverts = dense.data.vertices
for v in low.data.vertices:
    src = dverts[v.index] if v.index < NBASE else min(dverts[:NBASE], key=lambda d: (d.co - v.co).length_squared)
    ws = [(dname[ge.group], ge.weight) for ge in src.groups if dname[ge.group] in bone_names and dname[ge.group] != "jaw"]
    tot = sum(w for _, w in ws)
    if tot <= 1e-6:
        ws, tot = [("head" if v.co.z > 1.45 else "spine2", 1.0)], 1.0
    for n, w in ws:
        vg[n].add([v.index], w / tot, "REPLACE")
print("LOW weights copied; thigh.R verts",
      sum(1 for v in low.data.vertices for ge in v.groups if ge.group == vg["thigh.R"].index))
jaw_g, head_g = vg["jaw"], vg["head"]


def along_cut(x, k):
    x = max(cutp[0][0], min(cutp[-1][0], x))
    for a, b in zip(cutp, cutp[1:]):
        if a[0] <= x <= b[0]:
            t = 0 if b[0] == a[0] else (x - a[0]) / (b[0] - a[0])
            return a[k] + (b[k] - a[k]) * t
    return cutp[-1][k]


X_END = max(abs(c[0]) for c in cutp)
MOUTH_X = max(abs(p.x) for p in upper_pts)
tear_up_s, tear_lo_s = set(tear_up), set(tear_lo)
jaw_w = {}
for v in low.data.vertices:
    p = v.co
    if p.z < 1.38 or abs(p.x) > 0.095 or p.y > 0.01:
        continue
    zc, ycut = along_cut(p.x, 1), along_cut(p.x, 2)
    depth = p.y - (ycut + 0.012)
    if v.index in tear_lo_s:
        ws = 1.0
    elif v.index in tear_up_s:
        ws = 0.0
    elif depth < 0 and abs(p.x) <= MOUTH_X + 0.001 and abs(p.z - zc) < 0.004:
        ws = 1.0 if v.normal.z > 0 else 0.0
    else:
        width = 0.0006 if depth < 0 else min(0.012, 0.0006 + depth * 0.3)
        width = max(width, 0.012 * smoothstep(X_END - 0.008, X_END + 0.004, abs(p.x)))
        ws = smoothstep(zc + width, zc - width, p.z)
    t = smoothstep(-0.1, -0.02, p.y)
    w = ws * smoothstep(-0.005, -0.035, p.y) * smoothstep(1.35 + 0.07 * t, 1.445 + 0.025 * t, p.z)
    if w > 0.001:
        jaw_w[v.index] = w
for vi, w in jaw_w.items():
    for ge in low.data.vertices[vi].groups:
        if ge.group != jaw_g.index:
            ge.weight *= (1 - w)
    jaw_g.add([vi], w, "REPLACE")
for v in low.data.vertices:  # skull + face ride the head bone only
    p = v.co
    if p.z < 1.44:
        continue
    z0 = 1.455 + 0.085 * smoothstep(-0.06, 0.04, p.y)
    f = smoothstep(z0, z0 + 0.035, p.z)
    if f <= 0:
        continue
    wj = next((ge.weight for ge in v.groups if ge.group == jaw_g.index), 0.0)
    wh = next((ge.weight for ge in v.groups if ge.group == head_g.index), 0.0)
    for ge in v.groups:
        if ge.group not in (head_g.index, jaw_g.index):
            ge.weight *= (1 - f)
    head_g.add([v.index], wh * (1 - f) + f * (1 - wj), "REPLACE")
print("LOW jaw verts", len(jaw_w))
low.parent = rig

# ---------------------------------------------------------------- rig upgrade: fingers + eyes
# Fingers: beyond the knuckles each finger is its own connected piece of mesh. Find the five pieces,
# run a 3-bone chain down each one's centreline, and weight the piece to its chain.
def seg_dist(p, a, b):
    ab = b - a
    t = max(0.0, min(1.0, (p - a).dot(ab) / max(ab.length_squared, 1e-12)))
    return (p - (a + ab * t)).length


bm = bmesh.new()
bm.from_mesh(low.data)
bm.verts.ensure_lookup_table()
finger_specs = []  # (side, finger index, [joint positions], [vert indices])
for side in ("L", "R"):
    hb = rig.data.bones[f"hand.{side}"]
    h, t = hb.head_local.copy(), hb.tail_local.copy()
    axis, L = (t - h).normalized(), (t - h).length
    hand_g = vg[f"hand.{side}"].index
    hand_verts = {v.index for v in low.data.vertices
                  for ge in v.groups if ge.group == hand_g and ge.weight > 0.2}
    s_of = lambda i: (bm.verts[i].co - h).dot(axis) / L

    def pieces(region):
        seen, out = set(), []
        for i in region:
            if i in seen:
                continue
            stack, comp = [i], []
            while stack:
                j = stack.pop()
                if j in seen:
                    continue
                seen.add(j)
                comp.append(j)
                stack += [e.other_vert(bm.verts[j]).index for e in bm.verts[j].link_edges
                          if e.other_vert(bm.verts[j]).index in region]
            out.append(comp)
        return sorted(out, key=len, reverse=True)

    # Four fingers: lowest cut (closest to the palm) that still splits them into four pieces.
    fingers, finger_cut = None, None
    for k in range(85, 50, -2):
        cut_s = k / 100
        ps = [p for p in pieces({i for i in hand_verts if s_of(i) > cut_s}) if len(p) >= 6]
        if len(ps) == 4:
            fingers, finger_cut = ps, cut_s
        elif fingers:
            break
    lateral = Vector((1 if side == "L" else -1, 0, 0))
    fingers.sort(key=lambda c: sum(bm.verts[i].co.dot(lateral) for i in c) / len(c))  # index -> pinky
    finger_set = {i for c in fingers for i in c}
    # Thumb: the piece that splits off the side of the palm, just before it merges back in.
    thumb, best = None, 0
    for k in range(65, 15, -2):
        ps = pieces({i for i in hand_verts if s_of(i) > k / 100 and i not in finger_set})
        if len(ps) >= 2 and len(ps[1]) > best:
            thumb, best = ps[1], len(ps[1])
    print("FINGERS", side, "four at cut %.2f" % finger_cut, "sizes", [len(c) for c in fingers], "thumb", len(thumb or []))

    knuckle_s = max(0.5, finger_cut - 0.12)
    for fi, comp in enumerate(fingers):
        ss = sorted(comp, key=s_of)
        n = len(ss)
        avg = lambda a, b: sum((bm.verts[i].co for i in ss[int(a * n):max(int(a * n) + 1, int(b * n))]),
                               Vector()) / max(1, max(int(a * n) + 1, int(b * n)) - int(a * n))
        base, tip = avg(0.0, 0.1), avg(0.94, 1.0)
        base = base - axis * (s_of(ss[0]) - knuckle_s) * L  # extend back to the knuckle line
        joints = [base, base.lerp(tip, 0.45), base.lerp(tip, 0.74), tip]
        finger_specs.append((side, fi + 1, joints, comp))
    if thumb:
        tc = sum((bm.verts[i].co for i in thumb), Vector()) / len(thumb)
        tip = max((bm.verts[i].co for i in thumb), key=lambda p: (p - h).length)
        root = h + axis * 0.18 * L
        root = root.lerp(tc, 0.35)
        joints = [root, root.lerp(tip, 0.45), root.lerp(tip, 0.75), tip]
        # The thumb's own piece plus nearby palm skin along its chain.
        near = {i for i in hand_verts if i not in finger_set and
                min(seg_dist(bm.verts[i].co, joints[k], joints[k + 1]) for k in range(3)) < 0.012}
        finger_specs.append((side, 0, joints, sorted(set(thumb) | near)))
bm.free()

eye_centres = []
for c in json.load(open(os.path.join(work, "cut.json")))["eyes"]:
    eye_centres.append(Vector(c))

select_only(rig)
bpy.ops.object.mode_set(mode="EDIT")
eb = rig.data.edit_bones
FNAMES = ["thumb", "index", "middle", "ring", "pinky"]
for side, fi, joints, comp in finger_specs:
    parent = eb[f"hand.{side}"]
    for k in range(3):
        b = eb.new(f"{FNAMES[fi]}_{k + 1}.{side}")
        b.head, b.tail = joints[k], joints[k + 1]
        b.parent = parent
        b.use_connect = k > 0
        b.inherit_scale = "NONE"
        parent = b
for i, c in enumerate(eye_centres):
    side = "L" if c.x > 0 else "R"
    b = eb.new(f"eye.{side}")
    b.head, b.tail = c, c + Vector((0, 0, 0.02))  # points up like the head bone, so it stretches with it
    b.parent = eb["head"]
    b.inherit_scale = "NONE"
bpy.ops.object.mode_set(mode="OBJECT")

for side, fi, joints, comp in finger_specs:
    names = [f"{FNAMES[fi]}_{k + 1}.{side}" for k in range(3)]
    groups = [vg.new(name=n) for n in names]
    hand_g = vg[f"hand.{side}"]
    for vi in comp:
        v = low.data.vertices[vi]
        w_hand = next((ge.weight for ge in v.groups if ge.group == hand_g.index), 0.0)
        d = [seg_dist(v.co, joints[k], joints[k + 1]) for k in range(3)]
        inv = [1.0 / max(x, 1e-4) ** 4 for x in d]
        # blend back into the palm over the first part of the proximal segment
        root = smoothstep(0.0, 0.35, (v.co - joints[0]).dot((joints[1] - joints[0]).normalized()) /
                          max((joints[1] - joints[0]).length, 1e-6))
        tot = sum(inv)
        for g, wi in zip(groups, inv):
            g.add([vi], w_hand * root * wi / tot, "REPLACE")
        hand_g.add([vi], w_hand * (1 - root), "REPLACE")
for c in eye_centres:  # a soft patch of sealed lid skin rides the eye bone
    side = "L" if c.x > 0 else "R"
    g = vg.new(name=f"eye.{side}")
    for v in low.data.vertices:
        d = (v.co - c).length
        if d < EYE_REACH:
            w = 0.85 * smoothstep(EYE_REACH, EYE_REACH * 0.35, d)
            for ge in v.groups:
                if ge.group != g.index:
                    ge.weight *= (1 - w)
            g.add([v.index], w, "REPLACE")
print("RIG fingers", len(finger_specs), "eyes", len(eye_centres), "bones", len(rig.data.bones))
for side in ("L", "R"):  # fingers stretch with their hand, eyes with the head
    for fn in FNAMES:
        for k in range(3):
            STRETCH[f"{fn}_{k + 1}.{side}"] = STRETCH[f"hand.{side}"].copy()
    STRETCH[f"eye.{side}"] = STRETCH["head"].copy()

# ---------------------------------------------------------------- suit scraps + teeth
suit = scraps_hi.copy()
suit.data = scraps_hi.data.copy()
suit.name = "SuitLow"
scene.collection.objects.link(suit)
suit.modifiers.clear()
suit.vertex_groups.clear()
suit.parent = None
suit.matrix_world = Matrix.Identity(4)
select_only(suit)
dec = suit.modifiers.new("Decimate", "DECIMATE")
dec.ratio = 0.09
bpy.ops.object.modifier_apply(modifier=dec.name)
dt = suit.modifiers.new("Weights", "DATA_TRANSFER")
dt.object = low
dt.use_vert_data = True
dt.data_types_verts = {"VGROUP_WEIGHTS"}
dt.vert_mapping = "POLYINTERP_NEAREST"
dt.layers_vgroup_select_src = "ALL"
dt.layers_vgroup_select_dst = "NAME"
bpy.ops.object.datalayout_transfer(modifier=dt.name)
bpy.ops.object.modifier_apply(modifier=dt.name)
# In the rest pose the hands hang beside the thighs, so trouser strips can pick up hand weights from
# the nearest-surface transfer. Anything below the chest follows the torso and legs only.
ARM = ("clavicle", "upperarm", "forearm", "hand", "thumb", "index", "middle", "ring", "pinky")
sname = {g.index: g.name for g in suit.vertex_groups}
for v in suit.data.vertices:
    if v.co.z > 1.12:
        continue
    arm = [ge for ge in v.groups if sname[ge.group].startswith(ARM)]
    if not arm:
        continue
    for ge in arm:
        ge.weight = 0.0
    rest_w = [ge for ge in v.groups if ge.weight > 0]
    tot = sum(ge.weight for ge in rest_w)
    if tot > 1e-6:
        for ge in rest_w:
            ge.weight /= tot
    else:
        suit.vertex_groups["pelvis"].add([v.index], 1.0, "REPLACE")
print("SUIT tris", sum(len(p.vertices) - 2 for p in suit.data.polygons))

teeth = []
for name in ("TeethUpper", "TeethLower"):
    src = bpy.data.objects[name]
    t = src.copy()
    t.data = src.data.copy()
    t.name = name + "Low"
    scene.collection.objects.link(t)
    t.modifiers.clear()
    t.parent = None
    t.matrix_world = Matrix.Identity(4)
    teeth.append(t)

for obj, mat in ((low, "Skin"), (suit, "Suit"), (teeth[0], "Teeth"), (teeth[1], "Teeth")):
    obj.data.materials.clear()
    obj.data.materials.append(bpy.data.materials.get(mat) or bpy.data.materials.new(mat))

# ---------------------------------------------------------------- bake the stretch into the rest pose
parts = [low, suit] + teeth
for o in parts:
    o.parent = rig
    o.matrix_parent_inverse = rig.matrix_world.inverted()
    m = next((m for m in o.modifiers if m.type == "ARMATURE"), None) or o.modifiers.new("Armature", "ARMATURE")
    m.object = rig
rig.data.pose_position = "POSE"
for pb in rig.pose.bones:  # stretch only: bone lengths and girth, no rotation
    pb.rotation_mode = "QUATERNION"
    pb.rotation_quaternion = (1, 0, 0, 0)
    pb.location = (0, 0, 0)
    pb.scale = STRETCH[pb.name]
bpy.context.view_layer.update()
for o in parts:
    select_only(o)
    bpy.ops.object.modifier_apply(modifier=next(m.name for m in o.modifiers if m.type == "ARMATURE"))
select_only(rig)
bpy.ops.object.mode_set(mode="POSE")
bpy.ops.pose.select_all(action="SELECT")
bpy.ops.pose.armature_apply(selected=False)
bpy.ops.object.mode_set(mode="OBJECT")
for o in parts:
    m = o.modifiers.new("Armature", "ARMATURE")
    m.object = rig

# ---------------------------------------------------------------- one UV atlas instead of 24 UDIM tiles
def pack_atlas(obj, head_boost):
    """Even out texel density on the stretched shape, enlarge the head islands, pack into 0-1."""
    me = obj.data
    uv = me.uv_layers["UVMap"].data
    # UV islands never cross UDIM tiles, so each tile can be moved as a block: crop it to its
    # contents and fit it into a grid cell. The head tiles (1001 and its mirror) get 2x2 cells.
    tiles = {}
    for p in me.polygons:
        cu = sum(uv[i].uv.x for i in p.loop_indices) / p.loop_total
        cv = sum(uv[i].uv.y for i in p.loop_indices) / p.loop_total
        tiles.setdefault((math.floor(cu), math.floor(cv)), []).append(p.index)
    big = [t for t in tiles if head_boost > 1 and t[0] == 0 and t[1] in (0, 2)]
    small = sorted(t for t in tiles if t not in big)
    cells = len(big) * 4 + len(small)
    n = max(2, math.ceil(math.sqrt(cells)))
    if big and n % 2:
        n += 1
    occupied, slots = set(), {}
    for t in big:  # 2x2 blocks first
        for gy in range(0, n - 1, 2):
            gx = next((gx for gx in range(0, n - 1, 2)
                       if not {(gx, gy), (gx + 1, gy), (gx, gy + 1), (gx + 1, gy + 1)} & occupied), None)
            if gx is not None:
                occupied |= {(gx, gy), (gx + 1, gy), (gx, gy + 1), (gx + 1, gy + 1)}
                slots[t] = (gx, gy, 2)
                break
    free = [(gx, gy) for gy in range(n) for gx in range(n) if (gx, gy) not in occupied]
    for t, (gx, gy) in zip(small, free):
        slots[t] = (gx, gy, 1)
    pad = 0.004
    for t, polys in tiles.items():
        gx, gy, span = slots[t]
        loops = [li for pi in polys for li in me.polygons[pi].loop_indices]
        u0 = min(uv[i].uv.x for i in loops); u1 = max(uv[i].uv.x for i in loops)
        v0 = min(uv[i].uv.y for i in loops); v1 = max(uv[i].uv.y for i in loops)
        cell = span / n
        k = (cell - 2 * pad) / max(u1 - u0, v1 - v0, 1e-6)
        ox, oy = gx / n + pad, gy / n + pad
        for i in loops:
            u = uv[i].uv
            uv[i].uv = (ox + (u.x - u0) * k, oy + (u.y - v0) * k)
    print("ATLAS", obj.name, "tiles", len(tiles), "grid", n)
    us = [d.uv for d in uv]
    print("ATLAS", obj.name, "u %.3f..%.3f v %.3f..%.3f" % (min(u.x for u in us), max(u.x for u in us),
                                                          min(u.y for u in us), max(u.y for u in us)))


pack_atlas(low, 1.7)
pack_atlas(suit, 1.0)

# Bake copies for texture baking (stretched rest shape, no rig).
for o, name in ((low, "BakeLowBody"), (suit, "BakeLowSuit")):
    c = o.copy()
    c.data = o.data.copy()
    c.name = name
    c.modifiers.clear()
    c.parent = None
    c.matrix_world = o.matrix_world.copy()
    scene.collection.objects.link(c)

# ---------------------------------------------------------------- idle pose: same orientations, no stretch
select_only(rig)
bpy.ops.object.mode_set(mode="POSE")
order = []
def walk(b):
    order.append(b.name)
    for c in b.children:
        walk(c)
for b in rig.data.bones:
    if b.parent is None:
        walk(b)
for name in order:
    bpy.context.view_layer.update()
    pb = rig.pose.bones[name]
    if name in POSED_ROT:
        rot = POSED_ROT[name]
    else:  # new bones (fingers, eyes) keep their rest offset from the posed parent
        par = pb.parent
        rot = (par.matrix.to_3x3().normalized() @ par.bone.matrix_local.to_3x3().inverted()
               @ pb.bone.matrix_local.to_3x3())
    m = rot.normalized().to_4x4()
    m.translation = pb.head
    pb.matrix = m
bpy.ops.object.mode_set(mode="OBJECT")
bpy.context.view_layer.update()


def lowest(objs):
    dg = bpy.context.evaluated_depsgraph_get()
    return min((o.evaluated_get(dg).matrix_world @ v.co).z for o in objs for v in o.evaluated_get(dg).data.vertices)


idle_floor = lowest([low])
rig.data.pose_position = "REST"
bpy.context.view_layer.update()
rest_floor = lowest([low])
rest_top = max((low.matrix_world @ v.co).z for v in low.data.vertices)
rig.data.pose_position = "POSE"
print("GAME rest floor %.3f top %.3f (height %.3f) idle floor %.3f" % (rest_floor, rest_top, rest_top - rest_floor, idle_floor))

# Remove the high-poly render objects; keep rig + game parts + bake copies.
for o in [dense, scraps_hi, bpy.data.objects["TeethUpper"], bpy.data.objects["TeethLower"]]:
    bpy.data.objects.remove(o, do_unlink=True)
with open(os.path.join(work, "game_meta.json"), "w") as fh:
    json.dump({"rest_floor": rest_floor, "idle_floor": idle_floor}, fh)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work, "06_game.blend"))
print("GAME_OK")
