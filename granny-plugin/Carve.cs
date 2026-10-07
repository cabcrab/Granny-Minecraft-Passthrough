using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// EXPERIMENTAL: Granny's house broken like Minecraft blocks. Minecraft reports each broken cell
    /// ({"t":"carve","c":[x,y,z,...]}); here the house's geometry inside that cube is cut away:
    /// - the drawn meshes: every triangle is clipped against the cube (pieces outside stay, the inside goes, lightmap UVs
    ///   and normals kept), so a real hole shows what is behind;
    /// - mesh collisions get the cut mesh; box collisions lined up with the world are split around the cube (else kept);
    /// - the cell never becomes Minecraft collision again.
    /// Not done: static-batched or unreadable meshes (they stay), Granny's NavMesh (she doesn't walk through holes).
    /// </summary>
    internal static class Carve
    {
        public static readonly HashSet<long> Broken = new HashSet<long>();
        private static readonly Dictionary<int, Mesh> OwnMesh = new Dictionary<int, Mesh>(); // MeshFilter/MeshCollider id -> our copy
        private static readonly HashSet<int> Warned = new HashSet<int>();
        private static readonly Collider[] _none = new Collider[0];

        public static void Reset()
        {
            Broken.Clear();
            OwnMesh.Clear();
            Replacements.Clear();
            OrigMesh.Clear(); OrigOwner.Clear(); Batched.Clear(); ReplOrig.Clear(); McToBatched.Clear();
            BoxRoots.Clear(); PieceRoot.Clear(); Cells.Clear();
            Warned.Clear();
        }

        /// <summary>F10 / config: tell Minecraft whether the house breaks.</summary>
        public static void SendState() => Link.Send("{\"t\":\"breakable\",\"on\":" + (Plugin.BreakableHouse.Value ? "true" : "false") + "}");

        public static void OnCarve(JsonElement m)
        {
            var c = m.GetProperty("c");
            int n = c.GetArrayLength();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int meshes = 0, colliders = 0;
            for (int i = 0; i + 2 < n; i += 3)
            {
                int x = c[i].GetInt32(), y = c[i + 1].GetInt32(), z = c[i + 2].GetInt32();
                long key = Scanner.Key(x, y, z);
                Broken.Add(key);
                Vector3 a = Coords.ToUnity(x, y, z), b = Coords.ToUnity(x + 1, y + 1, z + 1);
                var box = new Bounds((a + b) * 0.5f, new Vector3(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y), Mathf.Abs(b.z - a.z)));
                if (!Cells.TryGetValue(key, out var cell)) Cells[key] = cell = new Cell { Key = key, Box = box };
                cell.At = Time.time;
                CutCell(box, ref meshes, ref colliders, cell.Touched);
            }
            Plugin.L.LogInfo($"carve: {n / 3} cell(s), {meshes} mesh cut(s), {colliders} collider change(s), {sw.ElapsedMilliseconds} ms");
        }

        /// <summary>Not part of the house: characters, the player and what they carry, loose items, our Minecraft blocks.</summary>
        private static bool Excluded(Component c)
        {
            var t = c.transform;
            if (Core.PlayerRoot != null && t.IsChildOf(Core.PlayerRoot)) return true;
            foreach (var f in CamHook.Followers) if (f != null && t.IsChildOf(f)) return true;
            if (Core.MainCam != null && t.IsChildOf(Core.MainCam.transform.root)) return true;
            if (Enemies.IsEnemyPart(t)) return true;
            var rb = c.GetComponentInParent<Rigidbody>();
            if (rb != null && !rb.isKinematic) return true;
            int layer = c.gameObject.layer;
            return layer == 3 || layer == 5 || layer == Blocks.Layer && layer != 0;
        }

        /// <summary>
        /// Cuts one cube out of the house. `touched` collects what it changed (to put back later); with `only`, just
        /// those objects are cut (re-applying the holes still open after others closed).
        /// </summary>
        private static void CutCell(Bounds box, ref int meshes, ref int colliders, HashSet<int> touched, HashSet<int> only = null)
        {
            // drawn meshes
            var cutNow = new HashSet<int>();
            foreach (var mf0 in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
            {
                var mf = mf0;
                var r = mf.GetComponent<MeshRenderer>();
                if (r == null || !r.enabled || !r.bounds.Intersects(box) || Excluded(mf)) continue;
                int key = ReplOrig.TryGetValue(mf.GetInstanceID(), out int orig) ? orig : mf.GetInstanceID();
                if (only != null && !only.Contains(key)) continue;
                if (r.isPartOfStaticBatch)
                {
                    // merged with others by Unity: replaced by our own copy, rebuilt from its collision model
                    mf = Unbatch(mf, r);
                    if (mf == null) continue;
                }
                var mesh = Editable(mf.GetInstanceID(), mf.sharedMesh, mf);
                if (mesh == null) continue;
                touched.Add(key);
                if (CutMesh(mesh, mf.transform, box)) { meshes++; cutNow.Add(mesh.GetInstanceID()); }
                if (mf.sharedMesh != mesh) mf.sharedMesh = mesh;
            }
            // collisions
            var hits = Physics.OverlapBox(box.center, box.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            foreach (var col in hits)
            {
                if (col == null || Excluded(col) || Blocks.IsOurs(col)) continue;
                var mc = col.TryCast<MeshCollider>();
                if (mc != null)
                {
                    int key = McToBatched.TryGetValue(mc.GetInstanceID(), out int bm) ? bm : mc.GetInstanceID();
                    if (only != null && !only.Contains(key)) continue;
                    var mesh = Editable(mc.GetInstanceID(), mc.sharedMesh, mc);
                    if (mesh == null) continue;
                    touched.Add(key);
                    if (cutNow.Contains(mesh.GetInstanceID()) || CutMesh(mesh, mc.transform, box))
                    {
                        mc.sharedMesh = null;
                        mc.sharedMesh = mesh;
                        colliders++;
                    }
                    continue;
                }
                var bc = col.TryCast<BoxCollider>();
                if (bc != null)
                {
                    int key = PieceRoot.TryGetValue(bc.GetInstanceID(), out int root) ? root : bc.GetInstanceID();
                    if (only != null && !only.Contains(key)) continue;
                    if (SplitBox(bc, box, key)) { colliders++; touched.Add(key); }
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        private static float _nextRegen;

        /// <summary>Holes close after RegenSeconds (not while the player stands in them: they wait).</summary>
        public static void Update()
        {
            float wait = Plugin.HouseRegenSeconds.Value;
            if (wait <= 0f || Cells.Count == 0 || Time.time < _nextRegen) return;
            _nextRegen = Time.time + 0.5f;
            Bounds player = Core.CC != null ? Core.CC.bounds : new Bounds(Core.PlayerRoot != null ? Core.PlayerRoot.position : Vector3.zero, Vector3.zero);
            var due = new List<Cell>();
            foreach (var c in Cells.Values)
                if (Time.time - c.At >= wait && !c.Box.Intersects(player)) due.Add(c);
            if (due.Count > 0) Regenerate(due);
        }

        private static void Regenerate(List<Cell> due)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var keys = new HashSet<int>();
            var sb = new System.Text.StringBuilder("{\"t\":\"uncarve\",\"c\":[");
            bool first = true;
            foreach (var c in due)
            {
                Cells.Remove(c.Key);
                Broken.Remove(c.Key);
                keys.UnionWith(c.Touched);
                Scanner.Unkey(c.Key, out int x, out int y, out int z);
                sb.Append(first ? "" : ",").Append(x).Append(',').Append(y).Append(',').Append(z);
                first = false;
            }
            // Minecraft first (the cells may be house again), then Granny's geometry back, then the house's collision
            Link.Send(sb.Append("]}").ToString());
            Restore(keys);
            int meshes = 0, colliders = 0;
            foreach (var c in Cells.Values) // the holes still open in those same objects
            {
                bool shares = false;
                foreach (int k in keys) if (c.Touched.Contains(k)) { shares = true; break; }
                if (shares) CutCell(c.Box, ref meshes, ref colliders, c.Touched, keys);
            }
            foreach (var c in due) Scanner.Rescan(c.Key);
            Plugin.L.LogInfo($"carve: {due.Count} cell(s) grew back, {keys.Count} object(s) restored, {Cells.Count} hole(s) still open, {sw.ElapsedMilliseconds} ms");
        }

        /// <summary>Puts objects back exactly as Granny had them.</summary>
        private static void Restore(HashSet<int> keys)
        {
            foreach (int key in keys)
            {
                if (Batched.TryGetValue(key, out var b))
                {
                    if (b.Copy != null)
                    {
                        var nf = b.Copy.GetComponent<MeshFilter>();
                        if (nf != null) { OwnMesh.Remove(nf.GetInstanceID()); ReplOrig.Remove(nf.GetInstanceID()); }
                        UnityEngine.Object.Destroy(b.Copy);
                    }
                    if (b.R != null) { b.R.enabled = true; Replacements.Remove(b.R.GetInstanceID()); }
                    if (b.Mc != null)
                    {
                        b.Mc.sharedMesh = null;
                        b.Mc.sharedMesh = b.ColliderOrig;
                        OwnMesh.Remove(b.Mc.GetInstanceID());
                        McToBatched.Remove(b.Mc.GetInstanceID());
                    }
                    Batched.Remove(key);
                    continue;
                }
                if (OrigMesh.TryGetValue(key, out var orig) && OrigOwner.TryGetValue(key, out var owner) && owner != null)
                {
                    var mf = owner.TryCast<MeshFilter>();
                    if (mf != null) mf.sharedMesh = orig;
                    var mc = owner.TryCast<MeshCollider>();
                    if (mc != null) { mc.sharedMesh = null; mc.sharedMesh = orig; }
                    OwnMesh.Remove(key);
                    OrigMesh.Remove(key);
                    OrigOwner.Remove(key);
                    continue;
                }
                if (BoxRoots.TryGetValue(key, out var bx))
                {
                    if (bx.Holder != null) UnityEngine.Object.Destroy(bx.Holder);
                    if (bx.Bc != null) bx.Bc.enabled = true;
                    BoxRoots.Remove(key);
                    var gone = new List<int>();
                    foreach (var kv in PieceRoot) if (kv.Value == key) gone.Add(kv.Key);
                    foreach (int g in gone) PieceRoot.Remove(g);
                }
            }
        }

        private static readonly Dictionary<int, MeshFilter> Replacements = new Dictionary<int, MeshFilter>(); // batched renderer id -> our copy

        /// <summary>
        /// A static-batched mesh (Unity merged it with others; that merged mesh can't be read): if its collision model is
        /// readable (the house's rooms are), our own copy is built from it with the same materials and lightmap, the
        /// merged one stops being drawn, and the copy (shared with the collision) is what gets cut.
        /// </summary>
        public static MeshFilter Unbatch(MeshFilter mf, MeshRenderer r)
        {
            if (Replacements.TryGetValue(r.GetInstanceID(), out var done) && done != null) return done;
            var mc = mf.GetComponent<MeshCollider>();
            if (mc == null || mc.sharedMesh == null || !mc.sharedMesh.isReadable) { WarnOnce(mf, "static batched, no readable collision model"); return null; }
            var mats = r.sharedMaterials;
            var copy = UnityEngine.Object.Instantiate(mc.sharedMesh);
            copy.name = mc.sharedMesh.name + " (carved)";
            var go = new GameObject(mf.name + " (carvable)");
            go.layer = mf.gameObject.layer;
            go.transform.SetParent(mf.transform, false); // same place, rotation and scale as the original
            var nf = go.AddComponent<MeshFilter>();
            nf.sharedMesh = copy;
            var nr = go.AddComponent<MeshRenderer>();
            nr.sharedMaterials = mats;
            nr.lightmapIndex = r.lightmapIndex;
            nr.lightmapScaleOffset = r.lightmapScaleOffset;
            nr.shadowCastingMode = r.shadowCastingMode;
            nr.receiveShadows = r.receiveShadows;
            r.enabled = false;
            Batched[mf.GetInstanceID()] = new BatchedInfo { R = r, Mc = mc, ColliderOrig = mc.sharedMesh, Copy = go };
            ReplOrig[nf.GetInstanceID()] = mf.GetInstanceID();
            McToBatched[mc.GetInstanceID()] = mf.GetInstanceID();
            OwnMesh[nf.GetInstanceID()] = copy;
            OwnMesh[mc.GetInstanceID()] = copy; // the collision uses the same (cut) copy
            mc.sharedMesh = null;
            mc.sharedMesh = copy;
            Replacements[r.GetInstanceID()] = nf;
            Plugin.L.LogInfo($"carve: {mf.name} rebuilt from its collision model ({copy.vertexCount} vertices, {copy.subMeshCount} parts, {mats.Length} materials, uv2={copy.uv2.Length > 0})");
            return nf;
        }

        /// <summary>Test: swap the nearest static-batched mesh for its rebuilt copy (no cut), to compare the look.</summary>
        public static void DevUnbatchNearest()
        {
            MeshFilter best = null; float bd = float.MaxValue;
            Vector3 eye = Core.Cam.transform.position, fwd = Core.Cam.transform.forward;
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
            {
                var r = mf.GetComponent<MeshRenderer>();
                if (r == null || !r.enabled || !r.isPartOfStaticBatch || mf.GetComponent<MeshCollider>() == null) continue;
                if (!r.bounds.IntersectRay(new Ray(eye, fwd), out float d)) continue;
                if (d < bd) { bd = d; best = mf; }
            }
            if (best == null) { Plugin.L.LogInfo("dev: no batched mesh in front"); return; }
            Unbatch(best, best.GetComponent<MeshRenderer>());
        }

        private static void WarnOnce(Component c, string why)
        {
            if (Warned.Add(c.GetInstanceID())) Plugin.L.LogInfo($"carve: {c.name} can't be cut ({why})");
        }

        /// <summary>Our own copy of a mesh (the shared original is never touched), or null if it can't be read.</summary>
        private static Mesh Editable(int id, Mesh src, Component owner)
        {
            if (OwnMesh.TryGetValue(id, out var own) && own != null) return own;
            if (src == null) return null;
            if (!src.isReadable) { if (Warned.Add(id)) Plugin.L.LogInfo($"carve: mesh {src.name} is not readable"); return null; }
            own = UnityEngine.Object.Instantiate(src);
            own.name = src.name + " (carved)";
            OwnMesh[id] = own;
            // the original, to put back when the hole closes
            OrigMesh[id] = src;
            OrigOwner[id] = owner;
            var mf = owner.TryCast<MeshFilter>();
            if (mf != null) mf.sharedMesh = own;
            return own;
        }

        // ---------------------------------------------------------------------------------------------------------
        // what was changed, to put it back when holes close (keys: the scene component's instance id)
        private static readonly Dictionary<int, Mesh> OrigMesh = new Dictionary<int, Mesh>();
        private static readonly Dictionary<int, Component> OrigOwner = new Dictionary<int, Component>();
        private class BatchedInfo { public MeshRenderer R; public MeshCollider Mc; public Mesh ColliderOrig; public GameObject Copy; }
        private static readonly Dictionary<int, BatchedInfo> Batched = new Dictionary<int, BatchedInfo>(); // original MeshFilter id
        private static readonly Dictionary<int, int> ReplOrig = new Dictionary<int, int>();   // our copy's MeshFilter id -> original's
        private static readonly Dictionary<int, int> McToBatched = new Dictionary<int, int>(); // collision of a rebuilt mesh -> original MeshFilter id
        private class BoxInfo { public BoxCollider Bc; public GameObject Holder; }
        private static readonly Dictionary<int, BoxInfo> BoxRoots = new Dictionary<int, BoxInfo>(); // the scene's box collision id
        private static readonly Dictionary<int, int> PieceRoot = new Dictionary<int, int>();         // our box piece id -> scene box id
        private class Cell { public long Key; public Bounds Box; public float At; public readonly HashSet<int> Touched = new HashSet<int>(); }
        private static readonly Dictionary<long, Cell> Cells = new Dictionary<long, Cell>();

        // ---------------------------------------------------------------------------------------------------------
        private struct V
        {
            public Vector3 p; // world
            public Vector3 n;
            public Vector4 t;
            public Vector2 uv, uv2;
            public Color c;
        }

        private static V Lerp(V a, V b, float s) => new V
        {
            p = Vector3.Lerp(a.p, b.p, s), n = Vector3.Lerp(a.n, b.n, s), t = Vector4.Lerp(a.t, b.t, s),
            uv = Vector2.Lerp(a.uv, b.uv, s), uv2 = Vector2.Lerp(a.uv2, b.uv2, s), c = Color.Lerp(a.c, b.c, s)
        };

        /// <summary>Splits a polygon by an axis plane: the part on the inside side (towards the cube) and the part outside.</summary>
        private static void Split(List<V> poly, int axis, float value, bool insideIsGreater, List<V> inside, List<V> outside)
        {
            inside.Clear(); outside.Clear();
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                V a = poly[i], b = poly[(i + 1) % n];
                float da = (a.p[axis] - value) * (insideIsGreater ? 1f : -1f);
                float db = (b.p[axis] - value) * (insideIsGreater ? 1f : -1f);
                if (da >= 0f) inside.Add(a); else outside.Add(a);
                if ((da >= 0f) != (db >= 0f))
                {
                    V m = Lerp(a, b, da / (da - db));
                    inside.Add(m); outside.Add(m);
                }
            }
        }

        /// <summary>Cuts the cube out of a mesh. True if anything changed.</summary>
        private static bool CutMesh(Mesh mesh, Transform tr, Bounds box)
        {
            var l2w = tr.localToWorldMatrix;
            var w2l = tr.worldToLocalMatrix;
            var verts = new List<Vector3>(mesh.vertices);
            int vc = verts.Count;
            Vector3[] normalsA = mesh.normals; Vector4[] tangentsA = mesh.tangents;
            Vector2[] uvA = mesh.uv; Vector2[] uv2A = mesh.uv2; Color[] colA = mesh.colors;
            var normals = normalsA.Length == vc ? new List<Vector3>(normalsA) : null;
            var tangents = tangentsA.Length == vc ? new List<Vector4>(tangentsA) : null;
            var uv = uvA.Length == vc ? new List<Vector2>(uvA) : null;
            var uv2 = uv2A.Length == vc ? new List<Vector2>(uv2A) : null;
            var cols = colA.Length == vc ? new List<Color>(colA) : null;
            var world = new Vector3[vc];
            for (int i = 0; i < vc; i++) world[i] = l2w.MultiplyPoint3x4(verts[i]);

            bool changed = false;
            int subs = mesh.subMeshCount;
            var newTris = new List<int>[subs];
            var poly = new List<V>(8);
            var inside = new List<V>(12); var outside = new List<V>(12);
            for (int s = 0; s < subs; s++)
            {
                int[] tris = mesh.GetIndices(s); // GetTriangles is stripped from Granny's build
                var outTris = new List<int>(tris.Length);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                    Vector3 p0 = world[i0], p1 = world[i1], p2 = world[i2];
                    var tb = new Bounds(p0, Vector3.zero); tb.Encapsulate(p1); tb.Encapsulate(p2);
                    if (!tb.Intersects(box)) { outTris.Add(i0); outTris.Add(i1); outTris.Add(i2); continue; }
                    changed = true;
                    poly.Clear();
                    foreach (int k in new[] { i0, i1, i2 })
                        poly.Add(new V
                        {
                            p = world[k], n = normals != null ? normals[k] : Vector3.up, t = tangents != null ? tangents[k] : Vector4.zero,
                            uv = uv != null ? uv[k] : Vector2.zero, uv2 = uv2 != null ? uv2[k] : Vector2.zero, c = cols != null ? cols[k] : Color.white
                        });
                    // peel off the parts outside each of the 6 faces of the cube; what's inside all of them goes
                    var pieces = new List<List<V>>();
                    var cur = new List<V>(poly);
                    for (int f = 0; f < 6 && cur.Count >= 3; f++)
                    {
                        int axis = f >> 1;
                        bool greater = (f & 1) == 0;
                        float value = greater ? box.min[axis] : box.max[axis];
                        Split(cur, axis, value, greater, inside, outside);
                        if (outside.Count >= 3) pieces.Add(new List<V>(outside));
                        cur = new List<V>(inside);
                    }
                    foreach (var piece in pieces)
                    {
                        int baseIndex = verts.Count;
                        foreach (var v in piece)
                        {
                            verts.Add(w2l.MultiplyPoint3x4(v.p));
                            normals?.Add(v.n); tangents?.Add(v.t); uv?.Add(v.uv); uv2?.Add(v.uv2); cols?.Add(v.c);
                        }
                        for (int k = 1; k + 1 < piece.Count; k++) { outTris.Add(baseIndex); outTris.Add(baseIndex + k); outTris.Add(baseIndex + k + 1); }
                    }
                }
                newTris[s] = outTris;
            }
            if (!changed) return false;
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts.ToArray();
            if (normals != null) mesh.normals = normals.ToArray();
            if (tangents != null) mesh.tangents = tangents.ToArray();
            if (uv != null) mesh.uv = uv.ToArray();
            if (uv2 != null) mesh.uv2 = uv2.ToArray();
            if (cols != null) mesh.colors = cols.ToArray();
            for (int s = 0; s < subs; s++) mesh.SetTriangles(newTris[s].ToArray(), s);
            mesh.RecalculateBounds();
            return true;
        }

        // ---------------------------------------------------------------------------------------------------------
        /// <summary>A box collision lined up with the world: replaced by up to 6 boxes around the cube.</summary>
        private static bool SplitBox(BoxCollider bc, Bounds cut, int rootKey)
        {
            var t = bc.transform;
            Vector3 e = t.rotation.eulerAngles;
            bool aligned = Mathf.Abs(Mathf.DeltaAngle(e.x, 0)) < 0.5f && Mathf.Abs(Mathf.DeltaAngle(e.z, 0)) < 0.5f && Mathf.Abs(Mathf.DeltaAngle(e.y, Mathf.Round(e.y / 90f) * 90f)) < 0.5f;
            if (!aligned) { WarnOnce(bc, "rotated box collision"); return false; }
            Bounds b = bc.bounds;
            if (!b.Intersects(cut)) return false;
            var parts = new List<Bounds>();
            Vector3 min = b.min, max = b.max;
            // slab by slab along x, then y, then z
            for (int axis = 0; axis < 3; axis++)
            {
                if (cut.min[axis] > min[axis])
                {
                    var pmax = max; pmax[axis] = cut.min[axis];
                    var p = new Bounds(); p.SetMinMax(min, pmax); parts.Add(p);
                    min[axis] = cut.min[axis];
                }
                if (cut.max[axis] < max[axis])
                {
                    var pmin = min; pmin[axis] = cut.max[axis];
                    var p = new Bounds(); p.SetMinMax(pmin, max); parts.Add(p);
                    max[axis] = cut.max[axis];
                }
            }
            var holder = new GameObject(bc.name + " (carved)");
            holder.layer = bc.gameObject.layer;
            holder.transform.SetParent(t.parent, true);
            foreach (var p in parts)
            {
                if (p.size.x < 1e-3f || p.size.y < 1e-3f || p.size.z < 1e-3f) continue;
                var go = new GameObject("piece");
                go.layer = bc.gameObject.layer;
                go.transform.SetParent(holder.transform, false);
                go.transform.position = p.center;
                var nb = go.AddComponent<BoxCollider>();
                nb.size = p.size;
                nb.sharedMaterial = bc.sharedMaterial;
                PieceRoot[nb.GetInstanceID()] = rootKey;
            }
            // the scene's own box: remember it and its pieces (pieces of pieces hang under this holder too)
            if (rootKey == bc.GetInstanceID()) BoxRoots[rootKey] = new BoxInfo { Bc = bc, Holder = holder };
            bc.enabled = false;
            return true;
        }

        /// <summary>Test: which of Unity's mesh functions exist in Granny's build (some are stripped).</summary>
        public static void DevApi()
        {
            var m = new Mesh();
            void T(string what, Action a)
            {
                try { a(); Plugin.L.LogInfo("dev: mesh api ok: " + what); }
                catch (Exception e) { Plugin.L.LogInfo($"dev: mesh api FAILS: {what}: {e.GetType().Name} {e.Message}"); }
            }
            T("set vertices", () => m.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up });
            T("get vertices", () => { var v = m.vertices; });
            T("triangles set", () => m.triangles = new[] { 0, 1, 2 });
            T("subMeshCount", () => { var s = m.subMeshCount; });
            T("GetTriangles", () => { var t = m.GetTriangles(0); });
            T("SetTriangles", () => m.SetTriangles(new[] { 0, 1, 2 }, 0));
            T("GetIndices", () => { var t = m.GetIndices(0); });
            T("SetIndices", () => m.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0));
            T("normals", () => { m.normals = new[] { Vector3.up, Vector3.up, Vector3.up }; var n = m.normals; });
            T("uv", () => { m.uv = new Vector2[3]; var u = m.uv; });
            T("uv2", () => { m.uv2 = new Vector2[3]; var u = m.uv2; });
            T("tangents", () => { m.tangents = new Vector4[3]; var u = m.tangents; });
            T("colors", () => { m.colors = new Color[3]; var u = m.colors; });
            T("indexFormat", () => m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32);
            T("RecalculateBounds", () => m.RecalculateBounds());
            T("Instantiate mesh", () => { var c = UnityEngine.Object.Instantiate(m); });
            T("isReadable", () => { var r = m.isReadable; });
            T("new MeshFilter/Renderer", () => { var go = new GameObject("t"); go.AddComponent<MeshFilter>().sharedMesh = m; var r = go.AddComponent<MeshRenderer>(); r.lightmapIndex = r.lightmapIndex; r.lightmapScaleOffset = r.lightmapScaleOffset; UnityEngine.Object.Destroy(go); });
            T("OverlapBox", () => { var h = Physics.OverlapBox(Vector3.zero, Vector3.one, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore); });
            // the static-batched house meshes: what can be reached
            int shown = 0;
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
            {
                var r = mf.GetComponent<MeshRenderer>();
                if (r == null || !r.isPartOfStaticBatch || shown++ > 12) continue;
                var mc = mf.GetComponent<MeshCollider>();
                Plugin.L.LogInfo($"dev: batched {mf.name}: mesh '{(mf.sharedMesh != null ? mf.sharedMesh.name : "-")}' readable={(mf.sharedMesh != null && mf.sharedMesh.isReadable)} " +
                    $"collider={(mc != null ? (mc.sharedMesh != null ? mc.sharedMesh.name + " readable=" + mc.sharedMesh.isReadable + " verts=" + mc.sharedMesh.vertexCount + " subs=" + mc.sharedMesh.subMeshCount : "no mesh") : "none")} materials={r.sharedMaterials.Length} lightmap={r.lightmapIndex}");
            }
        }

        /// <summary>Test: count which of the house's meshes near the player could be cut.</summary>
        public static void DevInfo()
        {
            int readable = 0, batched = 0, unreadable = 0;
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
            {
                var r = mf.GetComponent<MeshRenderer>();
                if (r == null || Core.PlayerRoot == null || (r.bounds.center - Core.PlayerRoot.position).magnitude > 20f) continue;
                if (r.isPartOfStaticBatch) batched++;
                else if (mf.sharedMesh != null && mf.sharedMesh.isReadable) readable++;
                else unreadable++;
            }
            Plugin.L.LogInfo($"dev: meshes near the player: {readable} cuttable, {batched} static batched, {unreadable} unreadable");
        }
    }
}
