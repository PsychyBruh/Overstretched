using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Overstretched
{
    /// <summary>
    /// Loads The Stretched from the embedded .lpm model (exported by tools/stretched2/08_export.py,
    /// already in Unity coordinates) and builds skinned instances of it at runtime.
    /// </summary>
    public static class StretchedModel
    {
        private const string Res = "Overstretched.Assets.Stretched.";

        public sealed class BoneData
        {
            public string Name;
            public int Parent;
            public Matrix4x4 Rest;
            public Matrix4x4 Idle;
        }

        public static IReadOnlyList<BoneData> Bones => bones;
        public static Mesh Mesh { get; private set; }

        private static List<BoneData> bones;
        private static string[] submeshMaterials;
        private static Material[] materials;

        /// <summary>Parses the model and builds the shared mesh. Safe to call repeatedly.</summary>
        public static void LoadMesh()
        {
            if (Mesh != null) return;
            using (var reader = new BinaryReader(Resource("Stretched.lpm"), Encoding.UTF8))
            {
                if (new string(reader.ReadChars(4)) != "LPM1")
                    throw new InvalidDataException("Stretched.lpm: bad magic");
                reader.ReadInt32(); // version

                int boneCount = reader.ReadInt32();
                bones = new List<BoneData>(boneCount);
                for (int i = 0; i < boneCount; i++)
                {
                    bones.Add(new BoneData
                    {
                        Name = ReadString(reader),
                        Parent = reader.ReadInt32(),
                        Rest = ReadMatrix(reader),
                        Idle = ReadMatrix(reader),
                    });
                }

                int vertexCount = reader.ReadInt32();
                var positions = new Vector3[vertexCount];
                var normals = new Vector3[vertexCount];
                var uvs = new Vector2[vertexCount];
                var weights = new BoneWeight[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    int b0 = reader.ReadInt32(), b1 = reader.ReadInt32(), b2 = reader.ReadInt32(), b3 = reader.ReadInt32();
                    weights[i] = new BoneWeight
                    {
                        boneIndex0 = b0, boneIndex1 = b1, boneIndex2 = b2, boneIndex3 = b3,
                        weight0 = reader.ReadSingle(), weight1 = reader.ReadSingle(),
                        weight2 = reader.ReadSingle(), weight3 = reader.ReadSingle(),
                    };
                }

                int submeshCount = reader.ReadInt32();
                submeshMaterials = new string[submeshCount];
                var triangles = new int[submeshCount][];
                for (int s = 0; s < submeshCount; s++)
                {
                    submeshMaterials[s] = ReadString(reader);
                    int count = reader.ReadInt32();
                    triangles[s] = new int[count];
                    for (int i = 0; i < count; i++) triangles[s][i] = reader.ReadInt32();
                }

                var mesh = new Mesh { name = "TheStretched", indexFormat = IndexFormat.UInt32 };
                mesh.vertices = positions;
                mesh.normals = normals;
                mesh.uv = uvs;
                mesh.boneWeights = weights;
                mesh.subMeshCount = submeshCount;
                for (int s = 0; s < submeshCount; s++) mesh.SetTriangles(triangles[s], s);
                mesh.bindposes = bones.Select(b => b.Rest.inverse).ToArray();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                mesh.UploadMeshData(true); // frees the CPU copy; triangles are no longer readable after this
                Mesh = mesh;
                Plugin.Log.LogInfo($"The Stretched model loaded: {vertexCount} verts, " +
                                   $"{triangles.Sum(t => t.Length) / 3} tris, {bones.Count} bones, submeshes [{string.Join(", ", submeshMaterials)}]");
            }
        }

        /// <summary>Builds a new instance in its idle pose. The root's origin is at the feet.</summary>
        public static GameObject Instantiate(Vector3 position, Quaternion rotation)
        {
            LoadMesh();
            if (materials == null) materials = submeshMaterials.Select(CreateMaterial).ToArray();

            var root = new GameObject("TheStretched");
            root.transform.SetPositionAndRotation(position, rotation);
            int layer = LayerMask.NameToLayer("Enemies");
            if (layer < 0) layer = 0;

            var transforms = new Transform[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                var t = new GameObject(bones[i].Name).transform;
                t.gameObject.layer = layer;
                transforms[i] = t;
            }
            for (int i = 0; i < bones.Count; i++)
            {
                var parent = bones[i].Parent >= 0 ? transforms[bones[i].Parent] : root.transform;
                transforms[i].SetParent(parent, false);
                SetLocal(transforms[i], bones[i].Rest, bones[i].Parent >= 0 ? bones[bones[i].Parent].Rest : Matrix4x4.identity);
            }

            var body = new GameObject("Body");
            body.layer = layer;
            body.transform.SetParent(root.transform, false);
            var smr = body.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = Mesh;
            smr.bones = transforms;
            smr.rootBone = transforms[0];
            smr.sharedMaterials = materials;
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = ShadowCastingMode.On;

            ApplyIdlePose(transforms);
            root.transform.localScale = Vector3.one * GameScale;
            var animator = root.AddComponent<StretchedAnimator>();
            animator.Init(transforms);
            animator.Play("idle", 0f);
            return root;
        }

        /// <summary>
        /// The model is authored at human scale (base mesh 1.68 m); the game's player capsule is 2.5 units,
        /// so everything is scaled up to match: about 3.7 units tall standing straight.
        /// </summary>
        public const float GameScale = 1.42f;

        /// <summary>Puts every bone of an instance back into the hunched idle pose.</summary>
        public static void ApplyIdlePose(Transform[] transforms)
        {
            for (int i = 0; i < bones.Count; i++)
                SetLocal(transforms[i], bones[i].Idle, bones[i].Parent >= 0 ? bones[bones[i].Parent].Idle : Matrix4x4.identity);
        }

        private static void SetLocal(Transform t, Matrix4x4 modelSpace, Matrix4x4 parentModelSpace)
        {
            var local = parentModelSpace.inverse * modelSpace;
            t.localPosition = local.GetColumn(3);
            t.localRotation = local.rotation;
            t.localScale = Vector3.one;
        }

        // ------------------------------------------------------------------ materials

        private static Material CreateMaterial(string name)
        {
            var template = FindTemplateMaterial();
            var mat = template != null ? new Material(template) : new Material(Shader.Find("HDRP/Lit"));
            mat.name = "TheStretched_" + name;
            mat.SetColor("_BaseColor", Color.white);
            mat.SetColor("_EmissiveColor", Color.black);
            mat.SetTexture("_EmissiveColorMap", null);
            mat.SetFloat("_Metallic", 0f);
            switch (name)
            {
                case "Skin":
                    SetMaps(mat, "StretchedSkin_albedo.png", "StretchedSkin_normal.png", "StretchedSkin_mask.png");
                    break;
                case "Suit":
                    SetMaps(mat, "StretchedSuit_albedo.png", null, "StretchedSuit_mask.png");
                    break;
                default: // teeth: yellowed bone, a little glossy
                    mat.SetTexture("_BaseColorMap", Texture2D.whiteTexture);
                    mat.SetColor("_BaseColor", new Color(0.74f, 0.64f, 0.42f));
                    mat.SetTexture("_NormalMap", null);
                    mat.SetTexture("_MaskMap", SolidTexture(new Color(0f, 1f, 0f, 0.6f)));
                    break;
            }
            return mat;
        }

        private static void SetMaps(Material mat, string albedo, string normal, string mask)
        {
            mat.SetTexture("_BaseColorMap", LoadTexture(albedo, linear: false));
            if (normal != null)
            {
                mat.SetTexture("_NormalMap", LoadTexture(normal, linear: true));
                mat.SetFloat("_NormalScale", 1f);
                mat.EnableKeyword("_NORMALMAP");
                mat.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            else
            {
                mat.SetTexture("_NormalMap", null);
            }
            mat.SetTexture("_MaskMap", LoadTexture(mask, linear: true));
            mat.EnableKeyword("_MASKMAP");
            mat.SetFloat("_SmoothnessRemapMin", 0f);
            mat.SetFloat("_SmoothnessRemapMax", 1f);
            mat.SetFloat("_AORemapMin", 0f);
            mat.SetFloat("_AORemapMax", 1f);
        }

        /// <summary>
        /// HDRP materials created from scratch at runtime miss their shader-pass setup, so clone one of
        /// the game's own opaque HDRP/Lit materials, preferring one already using normal and mask maps
        /// (guaranteeing those shader variants exist in the build).
        /// </summary>
        private static Material FindTemplateMaterial()
        {
            var lit = Resources.FindObjectsOfTypeAll<Material>()
                .Where(m => m != null && m.shader != null && m.shader.name == "HDRP/Lit"
                            && m.renderQueue < 3000 && !m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"))
                .ToList();
            var best = lit.FirstOrDefault(m => m.IsKeywordEnabled("_NORMALMAP") && m.IsKeywordEnabled("_MASKMAP"))
                       ?? lit.FirstOrDefault(m => m.IsKeywordEnabled("_NORMALMAP"))
                       ?? lit.FirstOrDefault();
            Plugin.Log.LogInfo($"The Stretched material template: {(best != null ? best.name : "<none, using new HDRP/Lit>")}");
            return best;
        }

        private static Texture2D LoadTexture(string file, bool linear)
        {
            using (var stream = Resource(file))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = file };
                ImageConversion.LoadImage(tex, memory.ToArray(), true);
                tex.anisoLevel = 4;
                tex.wrapMode = TextureWrapMode.Clamp;
                return tex;
            }
        }

        private static Texture2D SolidTexture(Color c)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            tex.SetPixel(0, 0, c);
            tex.Apply(false, true);
            return tex;
        }

        // ------------------------------------------------------------------ parsing helpers

        private static Stream Resource(string file)
        {
            var stream = typeof(StretchedModel).Assembly.GetManifestResourceStream(Res + file);
            if (stream == null) throw new FileNotFoundException("Embedded resource missing: " + Res + file);
            return stream;
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static Matrix4x4 ReadMatrix(BinaryReader reader)
        {
            var m = new Matrix4x4();
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m[r, c] = reader.ReadSingle();
            return m;
        }
    }
}
