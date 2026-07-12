using System;
using System.IO;
using System.Linq;
using System.Text;
using UpkManager.Models.UpkFile.Engine.Mesh;

namespace UpkExtract;

// Custom binary mesh-pack format for shipping cell static meshes from the
// server to the WebView2 3D viewer. Goal is compact + zero-copy to BufferGeometry.
//
// Layout (little-endian):
//   header  (16 bytes):
//     "MHMP" magic                       (4 bytes)
//     version uint16 = 2                 (2 bytes)
//     meshCount uint16                   (2 bytes)
//     reserved uint64 = 0                (8 bytes)
//   per mesh:
//     nameLen uint16
//     name    UTF-8 (nameLen bytes)
//     vertexCount uint32
//     indexCount  uint32                 // multiple of 3
//     positions: vertexCount * 12 bytes  (Vector3 float, X Y Z)
//     normals:   vertexCount * 12 bytes  (Vector3 float)
//     uvs:       vertexCount *  8 bytes  (Vector2 float)
//     indices:   indexCount  *  4 bytes  (uint32 — promoted from ushort for unified JS Uint32Array)
//     v2 ONLY:
//       diffuseNameLen uint16
//       diffuseName    UTF-8 (full PathName like "Pkg.Group.Texture", empty = no material)
//     v3 ONLY:
//       normalNameLen   uint16; normalName   UTF-8   (UE3 normal map ref)
//       specularNameLen uint16; specularName UTF-8   (UE3 specular/roughness/metallic packed map)
//       emissiveNameLen uint16; emissiveName UTF-8   (UE3 self-illumination ref)
//
// The viewer streams an ArrayBuffer, parses header, builds one BufferGeometry per mesh.
// v1 reader stays valid (just stops after indices). v2 readers branch on the version field.
public static class MeshPack
{
    public const uint Magic = 0x504D484D; // "MHMP" little-endian
    public const ushort Version = 7;       // bumped: 6-slot Marvel Heroes material data

    public sealed class SectionOut
    {
        public uint FirstIndex;
        public uint NumTriangles;
        public string Diffuse  = "";
        public string Normal   = "";
        public string Specular = "";
        public string Emissive = "";
        // v7+: Marvel Heroes 6-slot channel-packed textures. See OAS
        // MeshPreviewGameMaterialResolver.TryResolveGameTextureSlot for the
        // naming pattern. Empty = slot absent → shader falls through.
        public string Smspsk    = "";   // specmult_specpow_skinmask
        public string Espa      = "";   // emissivespecpow
        public string Smrr      = "";   // specmultrimmaskrefl
        public string SpecColor = "";   // speccolor
    }

    public sealed class BoneOut
    {
        public string Name = "";
        public int ParentIndex;
        public float Px, Py, Pz;            // bind-pose position
        public float Qx, Qy, Qz, Qw;        // bind-pose orientation (quat)
    }

    public sealed class MeshOut
    {
        public string Name = "";
        public string DiffuseTextureName  = "";
        public string NormalTextureName   = "";
        public string SpecularTextureName = "";
        public string EmissiveTextureName = "";
        // v7+: mesh-level fallback for the 6-slot Marvel Heroes layout.
        public string SmspskTextureName    = "";
        public string EspaTextureName      = "";
        public string SmrrTextureName      = "";
        public string SpecColorTextureName = "";
        public float[] Positions = Array.Empty<float>();
        public float[] Normals   = Array.Empty<float>();
        public float[] UVs       = Array.Empty<float>();
        public float[] UVs2      = Array.Empty<float>();
        public uint[]  Indices   = Array.Empty<uint>();
        public SectionOut[] Sections = Array.Empty<SectionOut>();
        // v6 skinning. Empty for static meshes.
        public BoneOut[] Bones = Array.Empty<BoneOut>();
        public ushort[] SkinIndices = Array.Empty<ushort>();  // 4 per vert, RefSkeleton-absolute
        public byte[]   SkinWeights = Array.Empty<byte>();    // 4 per vert, /255 → weight
    }

    public static MeshOut FromStaticMesh(string name, FStaticMeshRenderData lod)
    {
        if (lod == null || lod.PositionVertexBuffer?.VertexData == null) return null;
        int vc = (int)lod.NumVertices;
        if (vc <= 0) return null;

        var pos = new float[vc * 3];
        var nrm = new float[vc * 3];
        var uvs = new float[vc * 2];
        var uvs2 = new float[vc * 2];   // UV1 = lightmap UV
        bool hasUv2 = false;

        for (int i = 0; i < vc; i++)
        {
            var p = lod.PositionVertexBuffer.VertexData[i].Position;
            pos[i*3 + 0] = p.X; pos[i*3 + 1] = p.Y; pos[i*3 + 2] = p.Z;
        }
        if (lod.VertexBuffer?.VertexData != null)
        {
            int i = 0;
            uint numTC = lod.VertexBuffer.NumTexCoords;
            hasUv2 = numTC >= 2;
            foreach (var v in lod.VertexBuffer.VertexData)
            {
                if (i >= vc) break;
                nrm[i*3 + 0] = v.TangentZ.X;
                nrm[i*3 + 1] = v.TangentZ.Y;
                nrm[i*3 + 2] = v.TangentZ.Z;
                var uv = v.GetVector2(0);
                uvs[i*2 + 0] = uv.X;
                uvs[i*2 + 1] = uv.Y;
                if (hasUv2)
                {
                    var uv1 = v.GetVector2(1);
                    uvs2[i*2 + 0] = uv1.X;
                    uvs2[i*2 + 1] = uv1.Y;
                }
                i++;
            }
        }

        var idxSrc = lod.IndexBuffer?.Indices;
        var idx = new uint[idxSrc != null ? idxSrc.Count : 0];
        if (idxSrc != null)
            for (int i = 0; i < idxSrc.Count; i++)
                idx[i] = idxSrc[i];

        return new MeshOut { Name = name, Positions = pos, Normals = nrm, UVs = uvs, UVs2 = hasUv2 ? uvs2 : Array.Empty<float>(), Indices = idx };
    }

    public static void WritePack(string outPath, MeshOut[] meshes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write(Magic);
        bw.Write(Version);
        bw.Write((ushort)meshes.Length);
        bw.Write((ulong)0);

        foreach (var m in meshes)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(m.Name);
            bw.Write((ushort)nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write((uint)(m.Positions.Length / 3));
            bw.Write((uint)m.Indices.Length);
            // Floats and uints — Span<byte> conversion via MemoryMarshal would be a touch faster
            // but BinaryWriter loops are clearer and fast enough for one-time per-cell extraction.
            for (int i = 0; i < m.Positions.Length; i++) bw.Write(m.Positions[i]);
            for (int i = 0; i < m.Normals.Length;   i++) bw.Write(m.Normals[i]);
            for (int i = 0; i < m.UVs.Length;       i++) bw.Write(m.UVs[i]);
            for (int i = 0; i < m.Indices.Length;   i++) bw.Write(m.Indices[i]);

            // v2+: per-mesh diffuse texture PathName.
            WriteShortStr(bw, m.DiffuseTextureName);
            // v3+: normal / specular / emissive.
            WriteShortStr(bw, m.NormalTextureName);
            WriteShortStr(bw, m.SpecularTextureName);
            WriteShortStr(bw, m.EmissiveTextureName);
            // v7+: per-mesh Marvel Heroes 6-slot fallback.
            WriteShortStr(bw, m.SmspskTextureName);
            WriteShortStr(bw, m.EspaTextureName);
            WriteShortStr(bw, m.SmrrTextureName);
            WriteShortStr(bw, m.SpecColorTextureName);
            // v4+: lightmap UV (UV1). Count = vert count if present, else 0.
            int uv2Count = m.UVs2.Length / 2;
            bw.Write((uint)uv2Count);
            for (int i = 0; i < m.UVs2.Length; i++) bw.Write(m.UVs2[i]);
            // v5+: section table.
            bw.Write((uint)m.Sections.Length);
            foreach (var s in m.Sections)
            {
                bw.Write(s.FirstIndex);
                bw.Write(s.NumTriangles);
                WriteShortStr(bw, s.Diffuse);
                WriteShortStr(bw, s.Normal);
                WriteShortStr(bw, s.Specular);
                WriteShortStr(bw, s.Emissive);
                // v7+: per-section Marvel Heroes 6-slot textures.
                WriteShortStr(bw, s.Smspsk);
                WriteShortStr(bw, s.Espa);
                WriteShortStr(bw, s.Smrr);
                WriteShortStr(bw, s.SpecColor);
            }
            // v6+: skinning data. Skipped (count=0) for static meshes.
            bw.Write((uint)m.Bones.Length);
            foreach (var b in m.Bones)
            {
                WriteShortStr(bw, b.Name);
                bw.Write(b.ParentIndex);
                bw.Write(b.Px); bw.Write(b.Py); bw.Write(b.Pz);
                bw.Write(b.Qx); bw.Write(b.Qy); bw.Write(b.Qz); bw.Write(b.Qw);
            }
            bw.Write((uint)(m.SkinIndices.Length / 4));
            for (int i = 0; i < m.SkinIndices.Length; i++) bw.Write(m.SkinIndices[i]);
            for (int i = 0; i < m.SkinWeights.Length; i++) bw.Write(m.SkinWeights[i]);
        }
    }

    private static void WriteShortStr(BinaryWriter bw, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s ?? "");
        bw.Write((ushort)b.Length);
        bw.Write(b);
    }
}
