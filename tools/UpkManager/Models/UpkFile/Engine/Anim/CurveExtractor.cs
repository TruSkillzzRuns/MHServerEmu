// Direct byte-walker for the CurveData ArrayProperty inside a game AnimSequence
// export body. Referenced from upstream AnimationParser.ParseCurveDataArray.
//
// WHY THIS EXISTS:
//   UAnimSequence declares [UnrealStruct("RawCurveTracks")] with CurveName +
//   CurveWeights — the correct wire shape. But the upstream typed-property
//   path doesn't always materialize a walkable Fields/Properties collection
//   the existing ExtractMorphCurvesFromProperties reflection harvester can
//   read, so it yields zero for game sequences (the comment in UAnimSequence.cs
//   line 86-89 admits this).
//
//   MHE ignores the typed-property layer entirely and walks the raw export
//   body bytes looking for the tagged CurveData property. That path works
//   regardless of whether the typed deserializer ran or how it shaped the
//   result. We port that here.
//
// CALLED FROM: UAnimSequence.ReadBuffer, after base.ReadBuffer has parsed
// properties. Idempotent — clears any previously-populated MorphCurves
// when it finds the CurveData property so the byte path beats stale or
// empty reflection output.

using System;
using System.Collections.Generic;
using UpkManager.Models.UpkFile.Tables;

namespace UpkManager.Models.UpkFile.Engine.Anim;

internal static class CurveExtractor
{
    /// <summary>
    /// Walk the export body bytes, find the CurveData tagged property, and
    /// populate <paramref name="output"/> with per-curve (Time, Value) keys.
    /// Returns true if CurveData was located (even if it had zero usable entries).
    /// </summary>
    public static bool Extract(
        byte[] exportBody,
        IReadOnlyList<UnrealNameTableEntry> nameTable,
        int numFrames,
        float sequenceLength,
        List<MorphCurveSample> output)
    {
        if (exportBody == null || exportBody.Length < 16 || nameTable == null) return false;

        int pos = 0;
        Skip(ref pos, 4); // NetIndex

        while (pos + 24 < exportBody.Length)
        {
            string propName = ReadFName(exportBody, ref pos, nameTable);
            if (propName == null) return false;
            if (propName.Equals("None", StringComparison.OrdinalIgnoreCase)) break;

            string propType = ReadFName(exportBody, ref pos, nameTable);
            if (propType == null) return false;

            int size = ReadInt32(exportBody, ref pos);
            Skip(ref pos, 4); // array index

            if (propName.Equals("CurveData", StringComparison.OrdinalIgnoreCase)
                && propType.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase))
            {
                int arrayEnd = pos + size;
                // The byte path is authoritative once we land it — clear any
                // partial output from the reflection harvester so we don't
                // double-emit curves.
                output.Clear();
                try
                {
                    ParseCurveArray(exportBody, ref pos, arrayEnd, nameTable, numFrames, sequenceLength, output);
                }
                catch
                {
                    pos = arrayEnd;
                }
                return true;
            }

            // Property skipping matches MHE's switch in ParseAnimSequence.
            switch (propType.ToLowerInvariant())
            {
                case "boolproperty":
                    Skip(ref pos, 1);
                    break;
                case "structproperty":
                    // 8 = struct-name FName, then the body of `size` bytes.
                    Skip(ref pos, 8 + size);
                    break;
                case "byteproperty":
                {
                    string enumName = ReadFName(exportBody, ref pos, nameTable);
                    if (enumName != null && enumName.Equals("None", StringComparison.OrdinalIgnoreCase))
                        Skip(ref pos, 1);
                    else
                        Skip(ref pos, size);
                    break;
                }
                default:
                    Skip(ref pos, size);
                    break;
            }
        }
        return false;
    }

    private static void ParseCurveArray(
        byte[] data,
        ref int pos,
        int end,
        IReadOnlyList<UnrealNameTableEntry> nameTable,
        int numFrames,
        float sequenceLength,
        List<MorphCurveSample> output)
    {
        int count = ReadInt32(data, ref pos);
        for (int i = 0; i < count && pos < end; i++)
        {
            string curveName = string.Empty;
            int curveTypeFlags = 0;
            float[] weights = null;

            while (pos < end)
            {
                string fName = ReadFName(data, ref pos, nameTable);
                if (fName == null) return;
                if (fName.Equals("None", StringComparison.OrdinalIgnoreCase)) break;

                string fType = ReadFName(data, ref pos, nameTable);
                if (fType == null) return;

                int fSize = ReadInt32(data, ref pos);
                Skip(ref pos, 4);

                if (fName.Equals("CurveName", StringComparison.OrdinalIgnoreCase)
                    && fType.Equals("NameProperty", StringComparison.OrdinalIgnoreCase))
                {
                    curveName = ReadFName(data, ref pos, nameTable) ?? string.Empty;
                }
                else if (fName.Equals("CurveWeights", StringComparison.OrdinalIgnoreCase)
                         && fType.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase))
                {
                    int n = ReadInt32(data, ref pos);
                    if (n < 0 || n > 100_000 || pos + n * 4 > end) return;
                    weights = new float[n];
                    for (int j = 0; j < n; j++)
                        weights[j] = ReadFloat(data, ref pos);
                }
                else if (fName.Equals("CurveTypeFlags", StringComparison.OrdinalIgnoreCase)
                         && fType.Equals("IntProperty", StringComparison.OrdinalIgnoreCase))
                {
                    curveTypeFlags = ReadInt32(data, ref pos);
                }
                else
                {
                    switch (fType.ToLowerInvariant())
                    {
                        case "boolproperty": Skip(ref pos, 1); break;
                        case "structproperty": Skip(ref pos, 8 + fSize); break;
                        case "byteproperty":
                        {
                            string enumName = ReadFName(data, ref pos, nameTable);
                            if (enumName != null && enumName.Equals("None", StringComparison.OrdinalIgnoreCase))
                                Skip(ref pos, 1);
                            else
                                Skip(ref pos, fSize);
                            break;
                        }
                        default: Skip(ref pos, fSize); break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(curveName) && weights is { Length: > 0 })
            {
                var sample = new MorphCurveSample
                {
                    CurveName = curveName,
                    CurveTypeFlags = curveTypeFlags,
                };
                // Map frame index → seconds in the sequence's actual time space.
                // MHE evaluates per-frame fractional and never converts to seconds;
                // your playback service samples by `timeline` (seconds), so we have
                // to bake the per-frame array against the sequence's real duration.
                // Hardcoded 30 fps here would desync any non-30 fps sequence.
                int last = Math.Max(1, weights.Length - 1);
                float secondsPerFrame = sequenceLength / last;
                for (int k = 0; k < weights.Length; k++)
                    sample.Keys.Add((k * secondsPerFrame, weights[k]));
                output.Add(sample);
            }
        }

        if (pos < end) pos = end;
    }

    private static string ReadFName(byte[] data, ref int pos, IReadOnlyList<UnrealNameTableEntry> nameTable)
    {
        if (pos + 8 > data.Length) return null;
        int idx = BitConverter.ToInt32(data, pos); pos += 4;
        pos += 4; // name number — MHE ignores it for resolution; we do too.
        if (idx < 0 || idx >= nameTable.Count) return null;
        var entry = nameTable[idx];
        if (entry?.Name == null) return string.Empty;
        return entry.Name.String ?? string.Empty;
    }

    private static int ReadInt32(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length) return 0;
        int v = BitConverter.ToInt32(data, pos); pos += 4;
        return v;
    }

    private static float ReadFloat(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length) return 0f;
        float v = BitConverter.ToSingle(data, pos); pos += 4;
        return v;
    }

    private static void Skip(ref int pos, int n) => pos += n;
}
