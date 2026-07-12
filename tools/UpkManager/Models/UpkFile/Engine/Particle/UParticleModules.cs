using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.Particle
{
    // The most-used particle modules in the TargetClient archive (top counts from our probe).
    // Properties are intentionally a focused subset â€” enough to drive a basic sprite
    // billboard simulator. Distributions use the existing FRawDistribution* infrastructure
    // (the baked LookupTable on those is the simplest path to actual numeric samples).

    public enum EParticleScreenAlignment
    {
        PSA_Square = 0,
        PSA_Rectangle = 1,
        PSA_Velocity = 2,
        PSA_TypeSpecific = 3,
        PSA_FacingCameraPosition = 4,
        PSA_MAX = 5
    }

    public enum EParticleSubUVInterpMethod
    {
        PSUVIM_None = 0,
        PSUVIM_Linear = 1,
        PSUVIM_Linear_Blend = 2,
        PSUVIM_Random = 3,
        PSUVIM_Random_Blend = 4,
        PSUVIM_MAX = 5
    }

    public enum EParticleSourceSelectionMethod
    {
        EPSSM_Random = 0,
        EPSSM_Sequential = 1,
        EPSSM_MAX = 2
    }

    [UnrealClass("ParticleModuleRequired")]
    public class UParticleModuleRequired : UParticleModule
    {
        [PropertyField]
        public EParticleScreenAlignment ScreenAlignment { get; set; }

        [PropertyField]
        public FObject Material { get; set; } // MaterialInterface

        [PropertyField]
        public bool bUseLocalSpace { get; set; }

        [PropertyField]
        public bool bKillOnDeactivate { get; set; }

        [PropertyField]
        public bool bKillOnCompleted { get; set; }

        [PropertyField]
        public EParticleSubUVInterpMethod InterpolationMethod { get; set; }

        [PropertyField]
        public int SubImages_Horizontal { get; set; } = 1;

        [PropertyField]
        public int SubImages_Vertical { get; set; } = 1;

        [PropertyField]
        public float EmitterDuration { get; set; }

        [PropertyField]
        public float EmitterDelay { get; set; }

        [PropertyField]
        public int EmitterRenderMode { get; set; }

        [PropertyField]
        public FVector EmitterOrigin { get; set; }

        [PropertyField]
        public FRotator EmitterRotation { get; set; }
    }

    [UnrealClass("ParticleModuleLifetime")]
    public class UParticleModuleLifetime : UParticleModule
    {
        [PropertyField]
        public FRawDistributionFloat Lifetime { get; set; }
    }

    [UnrealClass("ParticleModuleSize")]
    public class UParticleModuleSize : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector StartSize { get; set; }
    }

    [UnrealClass("ParticleModuleSizeMultiplyLife")]
    public class UParticleModuleSizeMultiplyLife : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector LifeMultiplier { get; set; }

        [PropertyField]
        public bool MultiplyX { get; set; } = true;

        [PropertyField]
        public bool MultiplyY { get; set; } = true;

        [PropertyField]
        public bool MultiplyZ { get; set; } = true;
    }

    [UnrealClass("ParticleModuleSizeScale")]
    public class UParticleModuleSizeScale : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector SizeScale { get; set; }
    }

    [UnrealClass("ParticleModuleVelocity")]
    public class UParticleModuleVelocity : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector StartVelocity { get; set; }

        [PropertyField]
        public FRawDistributionFloat StartVelocityRadial { get; set; }

        [PropertyField]
        public bool bInWorldSpace { get; set; }
    }

    [UnrealClass("ParticleModuleVelocityOverLifetime")]
    public class UParticleModuleVelocityOverLifetime : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector VelOverLife { get; set; }

        [PropertyField]
        public bool Absolute { get; set; }

        [PropertyField]
        public bool bInWorldSpace { get; set; }
    }

    [UnrealClass("ParticleModuleLocation")]
    public class UParticleModuleLocation : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector StartLocation { get; set; }
    }

    [UnrealClass("ParticleModuleLocationPrimitiveSphere")]
    public class UParticleModuleLocationPrimitiveSphere : UParticleModule
    {
        [PropertyField]
        public FRawDistributionFloat StartRadius { get; set; }

        [PropertyField]
        public bool Positive_X { get; set; } = true;

        [PropertyField]
        public bool Positive_Y { get; set; } = true;

        [PropertyField]
        public bool Positive_Z { get; set; } = true;

        [PropertyField]
        public bool Negative_X { get; set; } = true;

        [PropertyField]
        public bool Negative_Y { get; set; } = true;

        [PropertyField]
        public bool Negative_Z { get; set; } = true;
    }

    [UnrealClass("ParticleModuleLocationPrimitiveCylinder")]
    public class UParticleModuleLocationPrimitiveCylinder : UParticleModule
    {
        [PropertyField]
        public FRawDistributionFloat StartRadius { get; set; }

        [PropertyField]
        public FRawDistributionFloat StartHeight { get; set; }
    }

    [UnrealClass("ParticleModuleRotation")]
    public class UParticleModuleRotation : UParticleModule
    {
        [PropertyField]
        public FRawDistributionFloat StartRotation { get; set; }
    }

    [UnrealClass("ParticleModuleRotationRate")]
    public class UParticleModuleRotationRate : UParticleModule
    {
        [PropertyField]
        public FRawDistributionFloat StartRotationRate { get; set; }
    }

    [UnrealClass("ParticleModuleAcceleration")]
    public class UParticleModuleAcceleration : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector Acceleration { get; set; }

        [PropertyField]
        public bool bApplyOwnerScale { get; set; }
    }

    [UnrealClass("ParticleModuleColor")]
    public class UParticleModuleColor : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector StartColor { get; set; }

        [PropertyField]
        public FRawDistributionFloat StartAlpha { get; set; }

        [PropertyField]
        public bool bClampAlpha { get; set; } = true;
    }

    [UnrealClass("ParticleModuleColorScaleOverLife")]
    public class UParticleModuleColorScaleOverLife : UParticleModuleColorBase
    {
        [PropertyField]
        public FRawDistributionVector ColorScaleOverLife { get; set; }

        [PropertyField]
        public FRawDistributionFloat AlphaScaleOverLife { get; set; }

        [PropertyField]
        public bool bEmitterTime { get; set; }
    }

    [UnrealClass("ParticleModuleSubUV")]
    public class UParticleModuleSubUV : UParticleModule
    {
        [PropertyField]
        public FRawDistributionFloat SubImageIndex { get; set; }

        [PropertyField]
        public bool bUseRealTime { get; set; }
    }

    [UnrealClass("ParticleModuleCameraOffset")]
    public class UParticleModuleCameraOffset : UParticleModule
    {
        [PropertyField]
        public FRawDistributionFloat CameraOffset { get; set; }

        [PropertyField]
        public bool bSpawnTimeOnly { get; set; }
    }

    [UnrealClass("ParticleModuleOrientationAxisLock")]
    public class UParticleModuleOrientationAxisLock : UParticleModule
    {
        [PropertyField]
        public byte LockAxisFlags { get; set; }
    }

    [UnrealClass("ParticleModuleTypeDataMesh")]
    public class UParticleModuleTypeDataMesh : UParticleModule
    {
        [PropertyField]
        public FObject Mesh { get; set; } // StaticMesh

        [PropertyField]
        public bool CastShadows { get; set; }

        [PropertyField]
        public bool DoCollisions { get; set; }
    }

    [UnrealClass("ParticleModuleMeshRotation")]
    public class UParticleModuleMeshRotation : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector StartRotation { get; set; }

        [PropertyField]
        public bool bInheritParent { get; set; }
    }

    [UnrealClass("ParticleModuleMeshRotationRate")]
    public class UParticleModuleMeshRotationRate : UParticleModule
    {
        [PropertyField]
        public FRawDistributionVector StartRotationRate { get; set; }
    }

    // Phase 3: beam emitter support.
    //
    // UE3's beam architecture is fundamentally different from sprite/mesh
    // particles. A beam emitter has ONE logical particle per active beam,
    // and that "particle" carries source + target points + a tessellated
    // strip in between. The TypeDataBeam2 module is the marker that turns
    // the emitter into a beam emitter and supplies global beam parameters.
    public enum EBeam2Method
    {
        PEB2M_Distance = 0,    // beam extends a fixed distance forward
        PEB2M_Target = 1,      // beam connects to an explicit target point
        PEB2M_Branch = 2,      // beam branches from a parent beam emitter
        PEB2M_MAX = 3,
    }

    [UnrealClass("ParticleModuleTypeDataBeam2")]
    public class UParticleModuleTypeDataBeam2 : UParticleModule
    {
        [PropertyField] public EBeam2Method BeamMethod { get; set; }
        [PropertyField] public int TextureTile { get; set; } = 1;
        [PropertyField] public float TextureTileDistance { get; set; }
        [PropertyField] public int Sheets { get; set; } = 1;
        [PropertyField] public int MaxBeamCount { get; set; }
        [PropertyField] public float Speed { get; set; }
        [PropertyField] public int InterpolationPoints { get; set; }
        [PropertyField] public bool AlwaysOn { get; set; }
        [PropertyField] public FName BranchParentName { get; set; }
        // PEB2M_Distance: beam stretches this far along emitter forward.
        [PropertyField] public FRawDistributionFloat Distance { get; set; }
    }

    public enum EBeam2SourceTargetMethod
    {
        PEB2STM_Default = 0,
        PEB2STM_UserSet = 1,
        PEB2STM_Emitter = 2,
        PEB2STM_Particle = 3,
        PEB2STM_Actor = 4,
        PEB2STM_MAX = 5,
    }

    [UnrealClass("ParticleModuleBeamSource")]
    public class UParticleModuleBeamSource : UParticleModule
    {
        [PropertyField] public EBeam2SourceTargetMethod SourceMethod { get; set; }
        [PropertyField] public bool bSourceAbsolute { get; set; }
        [PropertyField] public FRawDistributionVector Source { get; set; }
        [PropertyField] public bool bLockSource { get; set; }
        [PropertyField] public FRawDistributionVector SourceTangent { get; set; }
        [PropertyField] public bool bLockSourceTangent { get; set; }
        [PropertyField] public FRawDistributionFloat SourceStrength { get; set; }
    }

    [UnrealClass("ParticleModuleBeamTarget")]
    public class UParticleModuleBeamTarget : UParticleModule
    {
        [PropertyField] public EBeam2SourceTargetMethod TargetMethod { get; set; }
        [PropertyField] public bool bTargetAbsolute { get; set; }
        [PropertyField] public FRawDistributionVector Target { get; set; }
        [PropertyField] public bool bLockTarget { get; set; }
        [PropertyField] public FRawDistributionVector TargetTangent { get; set; }
        [PropertyField] public bool bLockTargetTangent { get; set; }
        [PropertyField] public FRawDistributionFloat TargetStrength { get; set; }
    }

    [UnrealClass("ParticleModuleBeamNoise")]
    public class UParticleModuleBeamNoise : UParticleModule
    {
        [PropertyField] public bool bLowFreq_Enabled { get; set; }
        [PropertyField] public int Frequency { get; set; }
        [PropertyField] public float NoiseLockTime { get; set; }
        [PropertyField] public FRawDistributionVector NoiseRange { get; set; }
        [PropertyField] public float NoiseRangeScale { get; set; }
        [PropertyField] public float NoiseSpeed { get; set; }
        [PropertyField] public bool bNRScaleEmitterTime { get; set; }
        [PropertyField] public bool bSmooth { get; set; }
        [PropertyField] public float NoiseTension { get; set; }
        [PropertyField] public int NoiseTessellation { get; set; }
    }
}

