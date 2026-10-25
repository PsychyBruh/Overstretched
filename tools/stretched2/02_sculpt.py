# Step 2: turn the base mesh into The Stretched's rest-pose body.
#  - keep a low-res proxy (for bone-heat weights later)
#  - apply the multires sculpt at level 2 for anatomical detail
#  - seal the eyes: skin conformed over the eyeball shape, sunken
#  - emaciate: hollow cheeks/temples, ribs, collarbones, sunken belly
#  - cut the mouth slit and tear it back across both cheeks
#  - torn company-suit scraps (separate object, joined at rig time)
# Usage: blender -b 00_base.blend -P 02_sculpt.py -- <work_dir>
import bpy, bmesh, heapq, json, math, os, random, sys
from mathutils import Vector, noise

work = sys.argv[sys.argv.index("--") + 1]
scene = bpy.context.scene
body = bpy.data.objects["GEO-body_male_realistic"]
eyes = [o for o in bpy.data.objects if o.name.startswith("GEO-body_male_realistic.eye")]
eye_centers = [o.matrix_world.translation.copy() for o in eyes]
EYE_R = max(eyes[0].dimensions) / 2

def select_only(o):
    for x in scene.objects:
        x.select_set(False)
    o.select_set(True)
    bpy.context.view_layer.objects.active = o

# Bake the object transform so mesh coords == world coords from here on.
for o in eyes:
    o.parent = None
select_only(body)
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

# Low-res proxy for bone heat (no multires, original shape).
proxy = body.copy()
proxy.data = body.data.copy()
proxy.name = "Proxy"
scene.collection.objects.link(proxy)
proxy.modifiers.clear()

mr = next(m for m in body.modifiers if m.type == "MULTIRES")
mr.levels = mr.sculpt_levels = mr.render_levels = 2
select_only(body)
bpy.ops.object.modifier_apply(modifier=mr.name)
body.name = "Stretched"
for o in eyes:
    bpy.data.objects.remove(o, do_unlink=True)
print("DENSE verts", len(body.data.vertices))

me = body.data
bm = bmesh.new()
bm.from_mesh(me)
bm.verts.ensure_lookup_table()
bm.normal_update()


def smoothstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


def gauss(p, c, r):
    return math.exp(-((p - c).length_squared) / (r * r))


# ---------------------------------------------------------------- sealed eyes
R_SKIN, R_INNER = EYE_R * 1.02, EYE_R * 0.75
for c in eye_centers:
    region = []
    for v in bm.verts:
        d = v.co - c
        # The eye opening is an almond wider than tall: measure in a horizontally squashed space.
        de = Vector((d.x / 1.35, d.y, d.z)).length
        if de < EYE_R * 2.6 and d.y < EYE_R * 1.2:
            w = smoothstep(EYE_R * 2.6, EYE_R * 1.55, de) * smoothstep(EYE_R * 1.2, EYE_R * 0.3, d.y)
            inner = v.normal.dot(d) < 0  # surfaces facing the eyeball (inner lid folds)
            target = c + d.normalized() * (R_INNER if inner else R_SKIN)
            target.y += 0.004  # sink the whole socket
            region.append((v, w, inner, target))
    # Close the lids: pull everything toward the eye's horizontal midline (more at the centre of the
    # almond than at its corners), so the opening seals before the skin is stretched over the eyeball.
    CLOSE = 0.0062
    for v, w, inner, target in region:
        d = v.co - c
        taper = smoothstep(EYE_R * 1.9, EYE_R * 0.4, abs(d.x))
        dz = d.z
        dz = math.copysign(max(0.0, abs(dz) - CLOSE * taper * w), dz) if not inner else dz * (1 - w)
        v.co.z = c.z + dz
        target = c + (v.co - c).normalized() * (R_INNER if inner else R_SKIN)
        target.y += 0.004 + (0.003 if inner else 0.0)  # sink the socket; tuck the inner folds behind
        v.co = v.co.lerp(target, w)
    outer = [(v, w) for v, w, inner, _ in region if not inner and w > 0.02]
    for _ in range(12):  # relax so the lid outlines vanish under smooth, stretched skin
        new = {}
        for v, w in outer:
            nb = [e.other_vert(v).co for e in v.link_edges]
            new[v] = v.co.lerp(sum(nb, Vector()) / len(nb), 0.6 * w)
        for v, co in new.items():
            v.co = co
bm.normal_update()

# ---------------------------------------------------------------- emaciation
for v in bm.verts:
    p, n = v.co, v.normal
    d = 0.0
    ax = abs(p.x)
    if 1.42 < p.z < 1.72:  # face
        for s in (1, -1):
            d -= 0.007 * gauss(p, Vector((s * 0.052, -0.088, 1.495)), 0.022)  # hollow cheeks
            d -= 0.005 * gauss(p, Vector((s * 0.068, -0.055, 1.6)), 0.022)    # sunken temples
            d += 0.0025 * gauss(p, Vector((s * 0.058, -0.095, 1.535)), 0.012)  # sharp cheekbones
    if 1.06 < p.z < 1.3 and ax < 0.17 and n.y < 0.5:  # ribs: strongest on the flanks, under the pecs
        phase = (p.z + 0.6 * ax) / 0.034
        rib = max(0.0, math.sin(phase * 2 * math.pi)) ** 3
        mask = (smoothstep(1.06, 1.12, p.z) * smoothstep(1.3, 1.2, p.z)
                * smoothstep(0.03, 0.1, ax) * (0.5 + 0.5 * smoothstep(-0.05, 0.05, n.x * math.copysign(1, p.x))))
        d += 0.0034 * rib * mask
    if 0.94 < p.z < 1.12 and p.y < -0.03 and ax < 0.13:  # belly sucked in under the ribs
        d -= 0.016 * smoothstep(0.94, 1.0, p.z) * smoothstep(1.12, 1.07, p.z) * smoothstep(0.13, 0.05, ax)
    if 1.36 < p.z < 1.46 and p.y < -0.02:  # collarbones ridge + hollows above
        for s in (1, -1):
            a, b = Vector((s * 0.02, -0.085, 1.405)), Vector((s * 0.15, -0.05, 1.43))
            t = max(0.0, min(1.0, (p - a).dot(b - a) / (b - a).length_squared))
            dist = (p - a.lerp(b, t)).length
            d += 0.004 * math.exp(-(dist / 0.009) ** 2)
            d -= 0.004 * gauss(p, a.lerp(b, 0.5) + Vector((0, 0.01, 0.022)), 0.018)
    if d:
        v.co += n * d
bm.normal_update()

# ---------------------------------------------------------------- mouth slit + cheek tears
def dijkstra(src, goal_fn, allowed):
    dist, prev, heap = {src: 0.0}, {}, [(0.0, src.index, src)]
    while heap:
        dcur, _, v = heapq.heappop(heap)
        if goal_fn(v):
            path = [v]
            while path[-1] in prev:
                path.append(prev[path[-1]])
            return path[::-1]
        if dcur > dist.get(v, 1e9):
            continue
        for e in v.link_edges:
            u = e.other_vert(v)
            if not allowed(u):
                continue
            nd = dcur + e.calc_length()
            if nd < dist.get(u, 1e9):
                dist[u], prev[u] = nd, v
                heapq.heappush(heap, (nd, u.index, u))
    return None


face_region = lambda u: u.co.y < -0.02 and 1.44 < u.co.z < 1.56 and abs(u.co.x) < 0.085

# The base mesh already has a mouth bag: upper and lower lips are separate edge loops that meet at
# the mouth corners. Find them on the clean low-res proxy: start at the centre apex of each lip
# (local z-min for the upper lip, z-max for the lower) and walk the edge loop out to the corners.
pbm = bmesh.new()
pbm.from_mesh(proxy.data)


def lip_loop(sign):
    zone = [v for v in pbm.verts if abs(v.co.x) < 0.001 and 1.495 < v.co.z < 1.51 and v.co.y < -0.14]
    a = next(v for v in zone if all((e.other_vert(v).co.z - v.co.z) * sign >= -1e-5 for e in v.link_edges))
    pts = []
    for n in sorted((e.other_vert(a) for e in a.link_edges), key=lambda u: -abs(u.co.x - a.co.x))[:2]:
        loop, prev, cur = [], a, n
        while True:
            loop.append(cur.co.copy())
            if len(cur.link_edges) != 4 or len(loop) > 60:
                break
            ep = pbm.edges.get((prev, cur))
            nxt = [e for e in cur.link_edges if e is not ep and not (set(e.link_faces) & set(ep.link_faces))]
            if len(nxt) != 1:
                break
            prev, cur = cur, nxt[0].other_vert(cur)
        pts += loop
    return sorted(pts + [a.co.copy()], key=lambda p: p.x)


upper_curve, lower_curve = lip_loop(1), lip_loop(-1)
pbm.free()
outer = lambda u: u.normal.y < 0.2 and face_region(u)
corners = {s: min((u for u in bm.verts if outer(u)),
                  key=lambda u, c=(upper_curve[-1] if s > 0 else upper_curve[0]): (u.co - c).length) for s in (1, -1)}
print("LIPS upper", len(upper_curve), "lower", len(lower_curve),
      "corners", [tuple(round(c, 4) for c in corners[s].co) for s in (1, -1)])

cut_edges, tear_paths = [], {}
for side, corner in corners.items():  # tear from each mouth corner back toward the ear
    aim = Vector((side * 0.068, -0.06, 1.505))
    goal = min((u for u in bm.verts if face_region(u) and u.normal.x * side > 0.3), key=lambda u: (u.co - aim).length)
    seg = dijkstra(corner, lambda u, g=goal: u is g, face_region)
    tear_paths[side] = seg
    for a, b in zip(seg, seg[1:]):
        e = bm.edges.get((a, b))
        if e:
            cut_edges.append(e)
# Slit line (x, z, y) for jaw weighting: midline between the lips, then the tear paths.
mid = [((u.x + l.x) / 2, (u.z + l.z) / 2, (u.y + l.y) / 2) for u, l in
       zip(upper_curve, [min(lower_curve, key=lambda q, u=u: abs(q.x - u.x)) for u in upper_curve])]
tear_pts = {s: [(v.co.x, v.co.z, v.co.y) for v in tear_paths[s]] for s in (1, -1)}
cut_pts = sorted(mid + tear_pts[1] + tear_pts[-1])
bmesh.ops.split_edges(bm, edges=cut_edges)
bm.normal_update()

# Tear edges: upper side (stays with the skull) vs lower side (goes with the jaw), for teeth later.
tear_upper, tear_lower, upper, lower = [], [], [], []
for v in bm.verts:
    if v.is_boundary:
        fc = sum((f.calc_center_median() for f in v.link_faces), Vector()) / max(1, len(v.link_faces))
        if fc.z > v.co.z:
            tear_upper.append(v.co.copy())
            upper.append(v.index)
        else:
            tear_lower.append(v.co.copy())
            lower.append(v.index)
bm.to_mesh(me)
bm.free()
# Duplicated tear verts share positions, so tag each side by index for the jaw weights.
for name, idx in (("TearUpper", upper), ("TearLower", lower)):
    body.vertex_groups.new(name=name).add(idx, 1.0, "REPLACE")
print("CUT tear edges", len(cut_edges), "tear upper", len(tear_upper), "tear lower", len(tear_lower),
      "slit x range", round(cut_pts[0][0], 3), round(cut_pts[-1][0], 3))

# ---------------------------------------------------------------- torn suit scraps
def suit_piece(name, keep, seed, hole=0.3, tear_scale=10):
    piece = body.copy()
    piece.data = body.data.copy()
    piece.name = name
    piece.vertex_groups.clear()
    scene.collection.objects.link(piece)
    pb = bmesh.new()
    pb.from_mesh(piece.data)
    off = Vector((seed * 3.7, seed * 1.3, seed * 2.9))
    dead = []
    for f in pb.faces:
        c = f.calc_center_median()
        k = keep(c)
        if k <= 0 or noise.noise(c * tear_scale + off) + 0.35 * noise.noise(c * 40 + off) > hole + 0.6 * (k - 1):
            dead.append(f)
    bmesh.ops.delete(pb, geom=dead, context="FACES")
    # Drop tiny floating islands left by the tearing.
    pb.faces.ensure_lookup_table()
    seen, islands = set(), []
    for f in pb.faces:
        if f in seen:
            continue
        stack, isl = [f], []
        while stack:
            g = stack.pop()
            if g in seen:
                continue
            seen.add(g)
            isl.append(g)
            for e in g.edges:
                stack.extend(x for x in e.link_faces if x not in seen)
        islands.append(isl)
    small = [f for isl in islands if len(isl) < 120 for f in isl]
    bmesh.ops.delete(pb, geom=small, context="FACES")
    for v in pb.verts:
        # Loose fit with sagging horizontal folds and crumpled detail.
        fold = math.sin(v.co.z * 140 + 3 * noise.noise(v.co * 6 + off)) * 0.0016
        crumple = 0.0025 * max(0.0, noise.noise(v.co * 25 + off))
        v.co += v.normal * (0.0035 + fold + crumple)
    pb.to_mesh(piece.data)
    pb.free()
    sol = piece.modifiers.new("Thickness", "SOLIDIFY")
    sol.thickness = 0.0025
    sol.offset = 1
    select_only(piece)
    bpy.ops.object.modifier_apply(modifier=sol.name)
    return piece


ramp = lambda z, a, b: smoothstep(a, b, z)
def strap(c, a, b, half_width):
    """Mask for a band running from a to b (a torn strip of suit hanging across the body)."""
    a, b = Vector(a), Vector(b)
    t = max(0.0, min(1.0, (c - a).dot(b - a) / (b - a).length_squared))
    return smoothstep(half_width, half_width * 0.6, (c - a.lerp(b, t)).length)


_strip_rnd = random.Random(11)
STRIP_LEN = [_strip_rnd.uniform(0.08, 0.4) for _ in range(40)]


def waist_strips(c):
    """Torn waistband with ragged strips of trouser hanging down to random lengths."""
    if abs(c.x) > 0.2 or c.z > 0.975:
        return 0.0
    if c.z > 0.9:  # the waistband itself
        return 1.4 * ramp(c.z, 0.975, 0.955)
    leg_cx = 0.0 if c.z > 0.8 else math.copysign(0.1, c.x)  # around the pelvis, then around each leg
    ang = math.atan2(c.x - leg_cx, c.y + 0.01)
    n = 9 if c.z > 0.8 else 6
    pos = (ang / (2 * math.pi) + 0.5) * n + 0.35 * noise.noise(c * 14)
    strip = int(pos) + (0 if c.x < 0 else 20)
    frac = pos - int(pos)
    in_strip = 1.0 if 0.12 < frac < 0.7 else 0.0
    bottom = 0.9 - STRIP_LEN[strip % 40]
    return 1.3 * in_strip * ramp(c.z, bottom, bottom + 0.03)


pieces = [
    suit_piece("SuitPants", waist_strips, 1, hole=0.24, tear_scale=18),
    # Right sleeve stump.
    suit_piece("SuitSleeve", lambda c: (1.4 if c.x < -0.17 else 0) * ramp(c.z, 1.16, 1.22) * ramp(c.z, 1.44, 1.4), 2, hole=0.2),
    # A torn strip of chest fabric hanging diagonally from the left shoulder.
    suit_piece("SuitChest", lambda c: 1.45 * strap(c, (0.16, -0.02, 1.43), (-0.02, -0.11, 1.12), 0.065)
               * (1 if c.y < 0.04 else 0), 3, hole=0.12),
    # Ragged remains of the collar.
    suit_piece("SuitCollar", lambda c: (1.3 if abs(c.x) < 0.1 else 0) * ramp(c.z, 1.39, 1.41) * ramp(c.z, 1.46, 1.445), 4, hole=0.25),
]
for p in pieces:
    print("SCRAP", p.name, len(p.data.polygons), "faces")
select_only(pieces[0])
for p in pieces[1:]:
    p.select_set(True)
bpy.ops.object.join()
pieces[0].name = "SuitScraps"

bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work, "02_sculpt.blend"))
with open(os.path.join(work, "cut.json"), "w") as fh:
    json.dump({"cut": cut_pts, "eyes": [list(c) for c in eye_centers], "eye_r": EYE_R,
               "upper_lip": [list(p) for p in upper_curve], "lower_lip": [list(p) for p in lower_curve],
               "tear_upper": [list(p) for p in tear_upper], "tear_lower": [list(p) for p in tear_lower]}, fh)
print("SCULPT_OK")
