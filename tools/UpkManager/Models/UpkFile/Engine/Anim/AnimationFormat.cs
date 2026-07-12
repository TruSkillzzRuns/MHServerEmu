using System.Collections.Generic;
using UpkManager.Helpers;
using UpkManager.Models.UpkFile.Core;

namespace UpkManager.Models.UpkFile.Engine.Anim
{
    public enum AnimationCompressionFormat
    {
        ACF_None,                       // 0
        ACF_Float96NoW,                 // 1
        ACF_Fixed48NoW,                 // 2
        ACF_IntervalFixed32NoW,         // 3
        ACF_Fixed32NoW,                 // 4
        ACF_Float32NoW,                 // 5
        ACF_Identity,                   // 6
        ACF_MAX                         // 7
    };

    public enum AnimationKeyFormat
    {
        AKF_ConstantKeyLerp,            // 0
        AKF_VariableKeyLerp,            // 1
        AKF_PerTrackCompression,        // 2
        AKF_MAX                         // 3
    };

    public interface IAnimationCodec
    {
        void TranslationDecode(UAnimSequence sequence, ByteArrayReader reader, int numKeys);
        void RotationDecode(UAnimSequence sequence, ByteArrayReader reader, int numKeys);
    }

    public class AnimationFormat
    {
        public static bool SetInterfaceLinks(UAnimSequence sequence)
        {
            sequence.TranslationCodec = null;
            sequence.RotationCodec = null;

            // Both AKF_VariableKeyLerp and AKF_ConstantKeyLerp use the same decoded layout
            // per track — the only difference is that constant tracks have exactly one key
            // per track (no times array). VarKeyLerpCodec's RotationDecode/TranslationDecode
            // already short-circuit when numKeys == 1, so the same codec correctly handles
            // both encodings. Adding ConstantKeyLerp here makes static-pose sequences (the
            // ones that look like they "don't play") actually apply their single-key pose
            // instead of falling through to bind pose for lack of a codec.
            bool isLerpEncoding =
                sequence.KeyEncodingFormat == AnimationKeyFormat.AKF_VariableKeyLerp ||
                sequence.KeyEncodingFormat == AnimationKeyFormat.AKF_ConstantKeyLerp;

            if (isLerpEncoding)
            {
                // ACF_Identity means "no keys stored; use bind pose" — codec assignment
                // is still helpful for consistency but produces an empty track in either
                // case.
                if (sequence.TranslationCompressionFormat == AnimationCompressionFormat.ACF_None ||
                    sequence.TranslationCompressionFormat == AnimationCompressionFormat.ACF_Identity)
                    sequence.TranslationCodec = new VarKeyLerpCodec();
                if (sequence.RotationCompressionFormat == AnimationCompressionFormat.ACF_Float96NoW ||
                    sequence.RotationCompressionFormat == AnimationCompressionFormat.ACF_None ||
                    sequence.RotationCompressionFormat == AnimationCompressionFormat.ACF_Identity)
                    sequence.RotationCodec = new VarKeyLerpCodec();
            }

            if (sequence.TranslationCodec != null)
                sequence.TranslationData = [];

            if (sequence.RotationCodec != null)
            {
                sequence.RotationData = [];
                return true;
            }

            return false;
        }
    }

    public class AnimationEncodingCodec : IAnimationCodec
    {
        public static void Decompress(UAnimSequence sequence, byte[] compressedBytes)
        {
            var reader = ByteArrayReader.CreateNew(compressedBytes, 0);
            int numTracks = sequence.CompressedTrackOffsets.Length / 4;

            for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
            {
                int offsetTrans = sequence.CompressedTrackOffsets[trackIndex * 4 + 0];
                int numKeysTrans = sequence.CompressedTrackOffsets[trackIndex * 4 + 1];
                int offsetRot = sequence.CompressedTrackOffsets[trackIndex * 4 + 2];
                int numKeysRot = sequence.CompressedTrackOffsets[trackIndex * 4 + 3];

                reader.Seek(offsetTrans);
                sequence.TranslationCodec?.TranslationDecode(sequence, reader, numKeysTrans);

                reader.Seek(offsetRot);
                sequence.RotationCodec?.RotationDecode(sequence, reader, numKeysRot);
            }
        }

        public virtual void RotationDecode(UAnimSequence sequence, ByteArrayReader reader, int numKeys) { }
        public virtual void TranslationDecode(UAnimSequence sequence, ByteArrayReader reader, int numKeys) { }
    }

    public class VarKeyLerpCodec : AnimationEncodingCodec
    {
        public override void RotationDecode(UAnimSequence sequence, ByteArrayReader reader, int numKeys)
        {
            // Single-key tracks: the standard UE3 short-circuit is to treat them as
            // Float96NoW (3 floats, derive W). ACF_None tracks keep their own format
            // because they store the full 4-float quaternion regardless of key count.
            var format = sequence.RotationCompressionFormat;
            if (numKeys == 1 && format != AnimationCompressionFormat.ACF_None)
                format = AnimationCompressionFormat.ACF_Float96NoW;

            int numComponents = 3;
            bool hasExplicitW = false;

            if (format == AnimationCompressionFormat.ACF_IntervalFixed32NoW)
            {
                numComponents = 1;
                for (int i = 0; i < 6; i++)
                    reader.Skip(sizeof(float));
            }
            else if (format == AnimationCompressionFormat.ACF_None)
            {
                // Uncompressed FQuat: 4 raw floats per key, W stored explicitly.
                numComponents = 3;
                hasExplicitW = true;
            }

            var track = new RotationTrack();

            for (int k = 0; k < numKeys; k++)
            {
                float x = 0, y = 0, z = 0, w = 0;

                if (numComponents > 0) x = reader.ReadSingle();
                if (numComponents > 1) y = reader.ReadSingle();
                if (numComponents > 2) z = reader.ReadSingle();
                if (hasExplicitW) w = reader.ReadSingle();

                // FQuat's positional constructor takes (float, float, float, int) — likely a
                // legacy quirk — so we set X/Y/Z/W via the property setters to preserve the
                // explicit W stored by ACF_None tracks.
                track.RotKeys.Add(new FQuat { X = x, Y = y, Z = z, W = w });
            }

            TimeDecode(track.Times, sequence, reader, numKeys);

            sequence.RotationData.Add(track);
        }

        private void TimeDecode(List<float> times, UAnimSequence sequence, ByteArrayReader reader, int numKeys)
        {
            if (numKeys <= 1) return;

            // AKF_ConstantKeyLerp stores keys at UNIFORM intervals across the sequence and
            // does NOT serialize a per-key time array — keys are implicitly at frame indices
            // [0, NumFrames/N, 2*NumFrames/N, ..., NumFrames-1]. Reading time bytes here
            // picks up garbage from the next track's data, producing nonsense values like
            // t=120 on a 19-frame sequence and freezing animation playback at one key.
            // Only AKF_VariableKeyLerp serializes the per-track time array.
            if (sequence.KeyEncodingFormat == AnimationKeyFormat.AKF_ConstantKeyLerp)
            {
                float maxFrame = System.Math.Max(1, sequence.NumFrames - 1);
                for (int i = 0; i < numKeys; i++)
                {
                    float fraction = numKeys > 1 ? (float)i / (numKeys - 1) : 0f;
                    times.Add(maxFrame * fraction);
                }
                return;
            }

            reader.Align(4);

            bool useWord = sequence.NumFrames > 0xFF;

            for (int i = 0; i < numKeys; i++)
            {
                float time;
                if (useWord)
                {
                    ushort value = reader.ReadUInt16();
                    time = value;
                }
                else
                {
                    byte value = reader.ReadByte();
                    time = value;
                }
                times.Add(time);
            }

            reader.Align(4);
        }

        public override void TranslationDecode(UAnimSequence sequence, ByteArrayReader reader, int numKeys)
        {
            var format = numKeys == 1
                ? AnimationCompressionFormat.ACF_None
                : sequence.TranslationCompressionFormat;

            int numComponents = 3;

            if (format == AnimationCompressionFormat.ACF_IntervalFixed32NoW)
            {
                numComponents = 1;
                for (int i = 0; i < 6; i++)
                    reader.Skip(sizeof(float));
            }

            var track = new TranslationTrack();

            for (int k = 0; k < numKeys; k++)
            {
                float x = 0, y = 0, z = 0;

                if (numComponents > 0) x = reader.ReadSingle();
                if (numComponents > 1) y = reader.ReadSingle();
                if (numComponents > 2) z = reader.ReadSingle();

                track.PosKeys.Add(new FVector(x, y, z));
            }

            TimeDecode(track.Times, sequence, reader, numKeys);

            sequence.TranslationData.Add(track);
        }

    }
}
