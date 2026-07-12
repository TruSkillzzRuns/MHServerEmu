using System;
using System.Collections.Generic;
using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.Mesh
{
    // UE3 MorphTargetSet: container that references the base SkeletalMesh and an array
    // of per-morph-target exports. In UE3 source: Engine/Classes/MorphTargetSet.uc.
    [UnrealClass("MorphTargetSet")]
    public class UMorphTargetSet : UObject
    {
        [PropertyField]
        public UArray<FObject> Targets { get; set; } // UMorphTarget exports

        [PropertyField]
        public FObject BaseSkelMesh { get; set; } // USkeletalMesh
    }

    // UE3 MorphTarget: per-name sparse vertex-delta set, organized by LOD. The
    // MorphLODModels array is serialized as a binary tail AFTER the tagged
    // property block — UE3 streams the deltas as `TArray<FMorphTargetLODModel>`
    // using FArchive serialization. Each LOD model contains a sparse
    // `TArray<FMorphTargetVertInfo>` (PositionDelta, TangentZDelta, SourceIdx)
    // plus `INT NumBaseMeshVerts`.
    //
    // We expose MorphLODModels for callers, but parse it defensively — if the
    // binary tail isn't present in this build of game, the list stays empty and
    // morph application is a no-op.
    [UnrealClass("MorphTarget")]
    public class UMorphTarget : UObject
    {
        [PropertyField]
        public FObject BaseSkelMesh { get; set; } // back-ref to USkeletalMesh

        public List<FMorphTargetLODModel> MorphLODModels { get; } = new();

        // UE3 property tag for the morph target's name; falls back to the export's
        // ObjectName via the table entry when the property isn't serialized.
        public string MorphName { get; set; } = string.Empty;

        // Diagnostic: how many bytes remained after tagged-property parsing,
        // and the first 32 of them. Set in ReadBuffer so MorphTargetPalette
        // can surface them when the parser yields 0 vertices.
        public int BinaryTailRemaining { get; set; }
        public byte[] BinaryTailPeek { get; set; } = Array.Empty<byte>();

        public override void ReadBuffer(UBuffer buffer)
        {
            base.ReadBuffer(buffer);

            // Try to read the binary tail. UE3 layout after the tagged property
            // block is:
            //   INT LodModelCount
            //   for each LOD:
            //     INT VertexCount
            //     for each vert: FVector PositionDelta, FVector TangentZDelta, WORD SourceIdx
            //     INT NumBaseMeshVerts
            //
            // If the package doesn't actually serialize this (e.g. cooked builds
            // that elide MorphLODModels for some configs), the read positions
            // would walk past EOF and throw — wrap the whole tail in try/catch
            // and just leave the list empty.
            try
            {
                long remaining = buffer.Reader.Remaining;
                BinaryTailRemaining = (int)Math.Min(int.MaxValue, remaining);
                // Snapshot the first 32 bytes for diagnostic logging upstream.
                if (remaining > 0)
                {
                    int peekLen = (int)Math.Min(32, remaining);
                    int savedPos = buffer.Reader.CurrentOffset;
                    BinaryTailPeek = buffer.Reader.ReadBytes(peekLen);
                    buffer.Reader.Seek(savedPos);
                }
                if (remaining < 4)
                    return;

                int lodCount = buffer.Reader.ReadInt32();
                if (lodCount < 0 || lodCount > 8)
                    return;

                for (int lod = 0; lod < lodCount; lod++)
                {
                    int vertCount = buffer.Reader.ReadInt32();
                    if (vertCount < 0 || vertCount > 1_000_000)
                        return;

                    FMorphTargetLODModel lodModel = new();
                    // game uses the cooked-console 20-byte FMorphTargetVertInfo layout:
                    //   FVector PositionDelta (12) + FPackedNormal TangentZDelta (4) + INT SourceIdx (4)
                    // Verified across all 4 Venom morphs:
                    //   l_handscale3_fix  vertCount=492  → 492*20+12 = 9852 == tailRemaining ✓
                    //   mawtransition_fix vertCount=2532 → 2532*20+12 = 50652 ✓
                    //   r_handscale3_fix  vertCount=534  → 534*20+12 = 10692 ✓
                    //   squish            vertCount=4686 → 4686*20+12 = 93732 ✓
                    for (int v = 0; v < vertCount; v++)
                    {
                        FVector pos = FVector.ReadData(buffer);
                        // FPackedNormal: 4 signed bytes (-127..127 mapped to -1..1).
                        byte nx = buffer.Reader.ReadByte();
                        byte ny = buffer.Reader.ReadByte();
                        byte nz = buffer.Reader.ReadByte();
                        byte nw = buffer.Reader.ReadByte();
                        FVector tangentZ = new()
                        {
                            X = (nx - 127.5f) / 127.5f,
                            Y = (ny - 127.5f) / 127.5f,
                            Z = (nz - 127.5f) / 127.5f,
                        };
                        FMorphTargetDelta delta = new()
                        {
                            PositionDelta = pos,
                            TangentZDelta = tangentZ,
                            SourceIdx = buffer.Reader.ReadInt32(),
                        };
                        lodModel.Vertices.Add(delta);
                    }

                    if (buffer.Reader.Remaining >= 4)
                        lodModel.NumBaseMeshVerts = buffer.Reader.ReadInt32();

                    MorphLODModels.Add(lodModel);
                }
            }
            catch
            {
                // Binary layout drift — degrade to no-morph silently.
            }
        }
    }

    // Per-LOD container for a single morph target. UE3:
    // FMorphTargetLODModel { TArray<FMorphTargetVertInfo> Vertices; INT NumBaseMeshVerts; }
    public sealed class FMorphTargetLODModel
    {
        public List<FMorphTargetDelta> Vertices { get; } = new();
        public int NumBaseMeshVerts { get; set; }
    }

    // UE3 FMorphTargetVertInfo: 28 bytes — FVector PositionDelta (12) +
    // FVector TangentZDelta (12) + WORD SourceIdx (2) + WORD pad (alignment).
    // Some UE3 builds pack as 26 bytes (no pad). We read 26 explicitly and
    // let the caller handle alignment if a build differs.
    public sealed class FMorphTargetDelta
    {
        public FVector PositionDelta { get; set; }
        public FVector TangentZDelta { get; set; }
        // INT in game cooked layout (was WORD on PC UE3 source).
        public int SourceIdx { get; set; }
    }
}
