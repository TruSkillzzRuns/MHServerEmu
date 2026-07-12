using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.Anim
{
    [UnrealClass("AnimSet")]
    public class UAnimSet : UObject
    {
        [PropertyField]
        public UArray<FName> TrackBoneNames { get; set; }

        [PropertyField]
        public UArray<FObject> Sequences { get; set; } // UAnimSequence

        [PropertyField]
        public UArray<FName> UseTranslationBoneNames { get; set; }

        [PropertyField]
        public FName PreviewSkelMeshName { get; set; }
    }

    [UnrealClass("AnimNotify")]
    public class UAnimNotify : UObject
    {
    }

    [UnrealClass("AnimNotify_PlayParticleEffect")]
    public class UAnimNotify_PlayParticleEffect : UAnimNotify
    {
        [PropertyField]
        public FObject PSTemplate { get; set; } // ParticleSystem

        [PropertyField]
        public bool bAttach { get; set; }

        [PropertyField]
        public FName SocketName { get; set; }
    }

    [UnrealClass("MarvelAnimNotify_Footstep")]
    public class UMarvelAnimNotify_Footstep : UAnimNotify
    {
        [PropertyField]
        public int FootDown { get; set; }
    }

    [UnrealClass("AnimNotify_Trails")]
    public class UAnimNotify_Trails : UAnimNotify
    {
        [PropertyField]
        public FObject PSTemplate { get; set; } // ParticleSystem

        [PropertyField]
        public bool bSkipIfOwnerIsHidden { get; set; }

        [PropertyField]
        public FName FirstEdgeSocketName { get; set; }

        [PropertyField]
        public FName ControlPointSocketName { get; set; }

        [PropertyField]
        public FName SecondEdgeSocketName { get; set; }

        [PropertyField]
        public float LastStartTime { get; set; }

        [PropertyField]
        public float EndTime { get; set; }

        [PropertyField]
        public UArray<FTrailSample> TrailSampledData { get; set; }
    }

    [UnrealStruct("TrailSample")]
    public class FTrailSample : IAtomicStruct
    {
        [StructField]
        public float RelativeTime { get; set; }

        [StructField]
        public FVector FirstEdgeSample { get; set; }

        [StructField]
        public FVector ControlPointSample { get; set; }

        [StructField]
        public FVector SecondEdgeSample { get; set; }

        public string Format => "";
    }

    [UnrealStruct("AnimNotifyEvent")]
    public class FAnimNotifyEvent : IAtomicStruct
    {
        [StructField]
        public float Time { get; set; }

        [StructField("UAnimNotify")]
        public FObject Notify { get; set; } // UAnimNotify

        [StructField]
        public FName Comment { get; set; }

        [StructField]
        public float Duration { get; set; }

        public string Format => "";
    }


    // ------------------------------------------------------------------
    // UE3 curve-data tagged-property struct chain. Registered here so the
    // generic property parser can walk RawCurveData/CurveData on AnimSequence
    // and surface per-curve key arrays (morph-target weights, material params,
    // etc.) as strongly-typed property trees instead of returning <null>.
    //
    // Layout mirrors Engine/Classes/AnimSequence.uc + EngineAnimClasses.h:
    //   FRawCurveTracks { TArray<FFloatCurve> FloatCurves; }
    //   FAnimCurveBase  { FName CurveName; int CurveTypeFlags; ... }
    //   FFloatCurve : FAnimCurveBase { FRichCurve FloatCurve; }
    //   FRichCurve      { TArray<FRichCurveKey> Keys; float DefaultValue; ... }
    //   FRichCurveKey   { byte InterpMode/TangentMode/...; float Time/Value/... }
    //
    // We only need to register the property NAMES (per parent) for array-of-
    // struct fields so UArrayProperty can pick the right element factory; the
    // scalar fields self-describe via their tagged type in the wire stream.
    // The game build we target predates UE3's tangent-weight fields, so
    // FRichCurveKey stops at LeaveTangent.

    // game's "RawCurveTracks" struct (verified by runtime dump) holds the curve
    // INLINE — NOT a wrapper around TArray<FFloatCurve>. Each AnimSequence's
    // `curvedata` property is a UArray<FRawCurveTracks>, one element per curve.
    //   CurveName     : FName  — morph-target name to drive
    //   CurveWeights  : Array<float>[NumFrames]  — per-frame baked weights
    // No keyframes, no interpolation — the array is already sampled at the
    // sequence's frame rate (typically 30 fps), one weight per frame index.
    [UnrealStruct("RawCurveTracks")]
    public class FRawCurveTracks
    {
        [StructField]
        public FName CurveName { get; set; }

        [StructField]
        public UArray<float> CurveWeights { get; set; }
    }

    [UnrealStruct("AnimCurveBase")]
    public class FAnimCurveBase
    {
        [StructField]
        public FName CurveName { get; set; }

        [StructField]
        public int CurveTypeFlags { get; set; }

        [StructField]
        public int LastObservedNameHash { get; set; }
    }

    [UnrealStruct("FloatCurve")]
    public class FFloatCurve : FAnimCurveBase
    {
        [StructField]
        public FRichCurve FloatCurve { get; set; }
    }

    [UnrealStruct("RichCurve")]
    public class FRichCurve
    {
        [StructField]
        public UArray<FRichCurveKey> Keys { get; set; }

        [StructField]
        public float DefaultValue { get; set; }

        [StructField]
        public byte PreInfinityExtrap { get; set; }

        [StructField]
        public byte PostInfinityExtrap { get; set; }
    }

    [UnrealStruct("RichCurveKey")]
    public class FRichCurveKey
    {
        [StructField]
        public byte InterpMode { get; set; }

        [StructField]
        public byte TangentMode { get; set; }

        [StructField]
        public float Time { get; set; }

        [StructField]
        public float Value { get; set; }

        [StructField]
        public float ArriveTangent { get; set; }

        [StructField]
        public float LeaveTangent { get; set; }
    }


    [UnrealClass("AnimMetaData")]
    public class UAnimMetaData : UObject
    {
    }

    [UnrealClass("AnimMetaData_SkelControl")]
    public class UAnimMetaData_SkelControl : UAnimMetaData
    {
        [PropertyField]
        public UArray<FName> SkelControlNameList { get; set; }
    }
}
