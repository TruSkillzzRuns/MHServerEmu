using System;
using System.Globalization;
using System.Text;

namespace UpkManager.Models.UpkFile.Engine.Mesh
{
    // Deterministic 32-bit signature over a SkeletalMesh's RefSkeleton.
    // Tuple per bone: (index, name, parent, flags, position, orientation).
    // Identical inputs ALWAYS yield the identical hash; this is the canonical
    // "did the skeleton change?" check shared by UpkProbe, the repacker, and
    // the ORE retarget pipeline so all three agree on equality semantics.
    //
    // Float formatting uses InvariantCulture round-trip ("R") so a serialized
    // pose round-trips byte-identically. Hash is computed over UTF-8 text,
    // so the result is endian-neutral.
    public static class SkeletalMeshSignature
    {
        public static uint Compute(USkeletalMesh mesh)
        {
            if (mesh is null) throw new ArgumentNullException(nameof(mesh));

            var sb = new StringBuilder(capacity: 4096);
            var refSkel = mesh.RefSkeleton;
            if (refSkel == null) return Crc32(Array.Empty<byte>());

            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < refSkel.Count; i++)
            {
                var b = refSkel[i];
                var p = b.BonePos.Position;
                var q = b.BonePos.Orientation;
                sb.Append(i).Append('|')
                  .Append(b.Name?.Name).Append('|')
                  .Append(b.ParentIndex).Append('|')
                  .Append(b.Flags).Append('|')
                  .Append(p.X.ToString("R", inv)).Append(',')
                  .Append(p.Y.ToString("R", inv)).Append(',')
                  .Append(p.Z.ToString("R", inv)).Append('|')
                  .Append(q.X.ToString("R", inv)).Append(',')
                  .Append(q.Y.ToString("R", inv)).Append(',')
                  .Append(q.Z.ToString("R", inv)).Append(',')
                  .Append(q.W.ToString("R", inv)).Append(';');
            }
            return Crc32(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static uint Crc32(byte[] data)
        {
            const uint poly = 0xEDB88320u;
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    bool lsb = (crc & 1u) != 0;
                    crc >>= 1;
                    if (lsb) crc ^= poly;
                }
            }
            return ~crc;
        }
    }
}
