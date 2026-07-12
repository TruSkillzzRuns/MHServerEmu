using System;
using System.IO;
using System.Text;

namespace UpkExtract;

// MHAP: per-sequence animation pack. One UAnimSequence + its track→bone map.
//
// Layout (little-endian):
//   "MHAP" magic (4) | version u16 = 1 | reserved u16 = 0
//   nameLen u16 | name utf8           (sequence name)
//   sequenceLength f32 (seconds) | numFrames u32 | rateScale f32
//   trackCount u32
//   per track:
//     boneNameLen u16 | boneName utf8
//     posKeyCount u32; per key: time f32, x f32, y f32, z f32
//     rotKeyCount u32; per key: time f32, x f32, y f32, z f32, w f32
//
// Track i corresponds to TrackBoneNames[i] from the parent UAnimSet. The
// viewer matches boneName → THREE.Bone by name to build per-bone keyframe
// tracks for a THREE.AnimationClip.
public sealed class AnimTrack
{
    public string BoneName = "";
    public (float Time, float X, float Y, float Z)[] PosKeys = Array.Empty<(float, float, float, float)>();
    public (float Time, float X, float Y, float Z, float W)[] RotKeys = Array.Empty<(float, float, float, float, float)>();
}

public sealed class AnimOut
{
    public string Name = "";
    public float SequenceLength;
    public uint NumFrames;
    public float RateScale = 1.0f;
    public AnimTrack[] Tracks = Array.Empty<AnimTrack>();
}

public static class AnimPack
{
    public const uint Magic = 0x5041484D; // "MHAP" little-endian
    public const ushort Version = 1;

    public static void Write(string outPath, AnimOut a)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write(Magic);
        bw.Write(Version);
        bw.Write((ushort)0);

        WriteShortStr(bw, a.Name);
        bw.Write(a.SequenceLength);
        bw.Write(a.NumFrames);
        bw.Write(a.RateScale);

        bw.Write((uint)a.Tracks.Length);
        foreach (var t in a.Tracks)
        {
            WriteShortStr(bw, t.BoneName);
            bw.Write((uint)t.PosKeys.Length);
            foreach (var k in t.PosKeys)
            { bw.Write(k.Time); bw.Write(k.X); bw.Write(k.Y); bw.Write(k.Z); }
            bw.Write((uint)t.RotKeys.Length);
            foreach (var k in t.RotKeys)
            { bw.Write(k.Time); bw.Write(k.X); bw.Write(k.Y); bw.Write(k.Z); bw.Write(k.W); }
        }
    }

    private static void WriteShortStr(BinaryWriter bw, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s ?? "");
        bw.Write((ushort)b.Length);
        bw.Write(b);
    }
}
