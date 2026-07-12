using System;
using System.Collections.Generic;
using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.Anim
{
    [UnrealClass("AnimSequence")]
    public class UAnimSequence : UObject
    {
        [PropertyField]
        public FName SequenceName { get; set; }

        [PropertyField]
        public UArray<FAnimNotifyEvent> Notifies { get; set; }

        [PropertyField]
        public float SequenceLength { get; set; }

        [PropertyField]
        public int NumFrames { get; set; }

        [PropertyField]
        public float RateScale { get; set; } = 1.0f;

        [PropertyField]
        public AnimationCompressionFormat TranslationCompressionFormat { get; set; } = AnimationCompressionFormat.ACF_None;

        [PropertyField]
        public AnimationCompressionFormat RotationCompressionFormat { get; set; } = AnimationCompressionFormat.ACF_Float96NoW;

        [PropertyField]
        public AnimationKeyFormat KeyEncodingFormat { get; set; }

        [PropertyField]
        public int[] CompressedTrackOffsets { get; set; }

        // UE3 tagged-property container holding the per-sequence curve tracks
        // (morph weights, material scalars, custom event curves). Declared as
        // a real field so EngineRegistry resolves "RawCurveData" / "CurveData"
        // back to FRawCurveTracks during property-tree parsing — otherwise the
        // struct comes back <null> and downstream morph harvesting yields zero.
        [PropertyField]
        public FRawCurveTracks RawCurveData { get; set; }

        [PropertyField]
        public FRawCurveTracks CurveData { get; set; }


        [StructField("RawAnimSequenceTrack")]
        public UArray<RawAnimSequenceTrack> RawAnimationData { get; set; }

        [StructField("Data")]
        public byte[] CompressedByteStream { get; set; }


        [StructField("TranslationTrack")]
        public UArray<TranslationTrack> TranslationData { get; set; }

        [StructField("RotationTrack")]
        public UArray<RotationTrack> RotationData { get; set; }

        public IAnimationCodec TranslationCodec { get; set; }
        public IAnimationCodec RotationCodec { get; set; }

        // Pre-sampled morph-curve keyframes. Populated by the playback service
        // from the UE3 RawCurveData struct when it is present on the sequence's
        // tagged properties; left empty otherwise. Surfaced as a simple list so
        // the playback service can do linear sampling without re-walking property
        // trees every frame.
        public List<MorphCurveSample> MorphCurves { get; } = new();

        public override void ReadBuffer(UBuffer buffer)
        {
            base.ReadBuffer(buffer);

            RawAnimationData = buffer.ReadArray(RawAnimSequenceTrack.ReadData);

            CompressedByteStream = buffer.ReadBytes();

            if (AnimationFormat.SetInterfaceLinks(this))
                AnimationEncodingCodec.Decompress(this, CompressedByteStream);

            // Diagnostic: capture any bytes that remain after CompressedByteStream.
            // In game, morph-curve data (FRawCurveTracks / FFloatCurve[]) is most
            // likely serialized here as engine-private binary, NOT as a tagged
            // property — that's why ExtractMorphCurvesFromProperties yields zero.
            try
            {
                int rem = buffer.Reader.Remaining;
                BinaryTailRemaining = rem;
                if (rem > 0)
                {
                    int peekLen = Math.Min(64, rem);
                    int savedPos = buffer.Reader.CurrentOffset;
                    BinaryTailPeek = buffer.Reader.ReadBytes(peekLen);
                    buffer.Reader.Seek(savedPos);
                }
            }
            catch { /* diagnostic only */ }

            ExtractMorphCurvesFromProperties();

            // Byte-walk fallback. The reflection path above yields zero for
            // game sequences because the typed-property layer materializes
            // CurveData as C# objects with no Fields/Properties collection
            // for reflection to traverse. MHE bypasses this by walking the
            // export body directly; we do the same and let it overwrite any
            // partial reflection output when it finds CurveData.
            try
            {
                byte[] exportBody = buffer.Reader.GetBytes();
                if (exportBody is { Length: > 0 } && buffer.Header?.NameTable is { Count: > 0 } nt)
                {
                    CurveExtractor.Extract(exportBody, nt, NumFrames, SequenceLength, MorphCurves);
                }
            }
            catch { /* diagnostic-only fallback; never break sequence load */ }
        }

        // Diagnostic only — populated by ReadBuffer to inform morph-curve
        // binary-tail decoding work.
        public int BinaryTailRemaining { get; set; }
        public byte[] BinaryTailPeek { get; set; } = Array.Empty<byte>();

        // Walks the tagged-property tree for known UE3 curve containers and
        // pre-samples each curve into MorphCurves. Tolerant of multiple shapes:
        //   - "RawCurveData"  -> FRawCurveTracks -> FloatCurves[] of FFloatCurve
        //   - "CurveData"     -> similar
        //   - "MorphCurves"   -> simpler direct array (some pre-FRichCurve UE3 forks)
        // Anything we can't decode (missing keys, unfamiliar shape) is skipped.
        private void ExtractMorphCurvesFromProperties()
        {
            try
            {
                foreach (string containerName in new[] { "RawCurveData", "CurveData", "MorphCurves" })
                {
                    var prop = GetProperty(containerName);
                    if (prop is null)
                        continue;

                    // The property's Value may be either a struct (single
                    // FRawCurveTracks) or an array of float-curve structs.
                    // Walk reflectively — we don't have explicit type bindings
                    // for these legacy structures.
                    TryHarvestCurvesFromValue(prop.Value);
                }
            }
            catch
            {
                // Curve-extraction is best-effort; failures fall back to
                // "no morphs" rather than breaking sequence load.
            }
        }

        private void TryHarvestCurvesFromValue(object value)
        {
            if (value is null) return;

            // Drill into nested UStructProperty / EngineProperty / UArrayProperty
            // values to find lists of float-curve structs. We dispatch via
            // reflection because the property layer doesn't surface a strongly
            // typed FFloatCurve for us.
            var type = value.GetType();
            var fieldsProp = type.GetProperty("Fields") ?? type.GetProperty("Properties");
            var arrayProp = type.GetProperty("Array");
            var structProp = type.GetProperty("StructValue");

            if (arrayProp?.GetValue(value) is System.Collections.IEnumerable arr)
            {
                foreach (var item in arr)
                    TryHarvestCurvesFromValue(item);
                return;
            }

            if (structProp?.GetValue(value) is object structInner)
            {
                TryHarvestCurvesFromValue(structInner);
                return;
            }

            if (fieldsProp?.GetValue(value) is System.Collections.IEnumerable fields)
            {
                MorphCurveSample sample = new() { };
                string curveName = string.Empty;
                int flags = 0;
                var keys = new List<(float, float)>();
                bool sawCurve = false;

                foreach (var fld in fields)
                {
                    var fName = fld?.GetType().GetProperty("NameIndex")?.GetValue(fld) as Tables.FName;
                    var fValue = fld?.GetType().GetProperty("Value")?.GetValue(fld);
                    string nm = fName?.Name ?? string.Empty;

                    if (string.Equals(nm, "CurveName", StringComparison.OrdinalIgnoreCase))
                        curveName = ExtractScalarName(fValue);
                    else if (string.Equals(nm, "CurveTypeFlags", StringComparison.OrdinalIgnoreCase))
                        flags = ExtractScalarInt(fValue);
                    else if (string.Equals(nm, "FloatCurves", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(nm, "Curves", StringComparison.OrdinalIgnoreCase))
                    {
                        TryHarvestCurvesFromValue(fValue);
                        sawCurve = true;
                    }
                    else if (string.Equals(nm, "FloatCurve", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(nm, "Keys", StringComparison.OrdinalIgnoreCase))
                    {
                        HarvestKeysInto(fValue, keys);
                    }
                    else if (string.Equals(nm, "CurveWeights", StringComparison.OrdinalIgnoreCase))
                    {
                        // game's baked per-frame weights: Array<float>, one weight
                        // per frame index. Synthesize (frameIndex/30, weight) keys
                        // so the existing sampler interpolates them like normal.
                        HarvestWeightsArrayInto(fValue, keys);
                    }
                }

                if (!string.IsNullOrWhiteSpace(curveName) && keys.Count > 0)
                {
                    var entry = new MorphCurveSample { CurveName = curveName, CurveTypeFlags = flags };
                    entry.Keys.AddRange(keys);
                    MorphCurves.Add(entry);
                }
                else if (!sawCurve)
                {
                    // Fall through — no recognizable curve shape here.
                }
            }
        }

        // game bakes per-frame weights: Array<float>[NumFrames]. Convert to
        // (time, value) keys at 30 fps so SampleCurve interpolates naturally.
        private void HarvestWeightsArrayInto(object value, List<(float Time, float Value)> keys)
        {
            if (value is null) return;
            var arrayProp = value.GetType().GetProperty("Array");
            if (arrayProp?.GetValue(value) is not System.Collections.IEnumerable arr) return;
            int frame = 0;
            const float frameDt = 1.0f / 30.0f;
            foreach (var item in arr)
            {
                float w = ExtractScalarFloat(item?.GetType().GetProperty("Value")?.GetValue(item) ?? item);
                keys.Add((frame * frameDt, w));
                frame++;
            }
        }

        private static void HarvestKeysInto(object value, List<(float Time, float Value)> keys)
        {
            if (value is null) return;

            var type = value.GetType();
            var arrayProp = type.GetProperty("Array");
            var fieldsProp = type.GetProperty("Fields") ?? type.GetProperty("Properties");

            if (arrayProp?.GetValue(value) is System.Collections.IEnumerable arr)
            {
                foreach (var item in arr)
                {
                    var itemValue = item?.GetType().GetProperty("Value")?.GetValue(item) ?? item;
                    var itemFields = itemValue?.GetType().GetProperty("Fields")?.GetValue(itemValue)
                                  ?? itemValue?.GetType().GetProperty("Properties")?.GetValue(itemValue);
                    if (itemFields is System.Collections.IEnumerable kf)
                    {
                        float time = 0f, val = 0f;
                        foreach (var f in kf)
                        {
                            var nm = (f?.GetType().GetProperty("NameIndex")?.GetValue(f) as Tables.FName)?.Name ?? string.Empty;
                            var v = f?.GetType().GetProperty("Value")?.GetValue(f);
                            if (string.Equals(nm, "Time", StringComparison.OrdinalIgnoreCase)) time = ExtractScalarFloat(v);
                            else if (string.Equals(nm, "Value", StringComparison.OrdinalIgnoreCase)) val = ExtractScalarFloat(v);
                        }
                        keys.Add((time, val));
                    }
                }
            }
            else if (fieldsProp?.GetValue(value) is System.Collections.IEnumerable fields)
            {
                float time = 0f, val = 0f;
                foreach (var f in fields)
                {
                    var nm = (f?.GetType().GetProperty("NameIndex")?.GetValue(f) as Tables.FName)?.Name ?? string.Empty;
                    var v = f?.GetType().GetProperty("Value")?.GetValue(f);
                    if (string.Equals(nm, "Time", StringComparison.OrdinalIgnoreCase)) time = ExtractScalarFloat(v);
                    else if (string.Equals(nm, "Value", StringComparison.OrdinalIgnoreCase)) val = ExtractScalarFloat(v);
                }
                keys.Add((time, val));
            }
        }

        private static string ExtractScalarName(object v)
        {
            if (v is null) return string.Empty;
            var pv = v.GetType().GetProperty("PropertyValue")?.GetValue(v);
            if (pv is Tables.FName fn) return fn.Name ?? string.Empty;
            return pv?.ToString() ?? v.ToString() ?? string.Empty;
        }

        private static int ExtractScalarInt(object v)
        {
            if (v is null) return 0;
            var pv = v.GetType().GetProperty("PropertyValue")?.GetValue(v);
            return pv switch
            {
                int i => i,
                uint u => (int)u,
                byte b => b,
                _ => 0
            };
        }

        private static float ExtractScalarFloat(object v)
        {
            if (v is null) return 0f;
            var pv = v.GetType().GetProperty("PropertyValue")?.GetValue(v);
            return pv switch
            {
                float f => f,
                int i => i,
                _ => 0f
            };
        }
    }

    public class RawAnimSequenceTrack
    {
        public UArray<FVector> PosKeys { get; set; }
        public UArray<FQuat> RotKeys { get; set; }

        public static RawAnimSequenceTrack ReadData(UBuffer buffer)
        {
            var track = new RawAnimSequenceTrack
            {
                PosKeys = buffer.ReadArray(FVector.ReadData),
                RotKeys = buffer.ReadArray(FQuat.ReadData)
            };
            return track;
        }

        public override string ToString()
        {
            return $"PosKeys[{PosKeys.Count}] RotKeys[{RotKeys.Count}]";
        }
    }

    public class TranslationTrack
    {
        public UArray<FVector> PosKeys { get; set; } = [];
        public UArray<float> Times { get; set; } = [];

        public override string ToString()
        {
            int count = PosKeys.Count;
            string data = count > 0 ? PosKeys[0].Format : "";
            return $"{data} PosKeys[{count}] Times[{Times.Count}]";
        }
    }

    public class RotationTrack
    {
        public UArray<FQuat> RotKeys { get; set; } = [];
        public UArray<float> Times { get; set; } = [];

        public override string ToString()
        {
            int count = RotKeys.Count;
            string data = count > 0 ? RotKeys[0].Format : "";
            return $"{data} RotKeys[{RotKeys.Count}] Times[{Times.Count}]";
        }
    }

    // A single morph-target curve pre-sampled into a flat key list. Linear
    // interpolation between keys is sufficient for UE3 ACF_MorphTarget-flagged
    // curves; cubic tangents from FRichCurve are honored only if the source
    // import surfaces them (we treat unknown interpolation as linear).
    public sealed class MorphCurveSample
    {
        public string CurveName { get; init; } = string.Empty;

        // ACF_MorphTarget is bit 1 in UE3 FFloatCurve.CurveTypeFlags. We keep
        // the raw flags so callers can filter to only morph-flagged curves vs.
        // material/event float curves that share the same FRawCurveTracks
        // container.
        public int CurveTypeFlags { get; init; }

        public bool IsMorphTarget => (CurveTypeFlags & 0x2) != 0;

        public List<(float Time, float Value)> Keys { get; } = new();
    }
}
