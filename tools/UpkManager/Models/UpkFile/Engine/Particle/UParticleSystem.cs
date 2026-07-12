using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.Particle
{
    [UnrealClass("ParticleSystem")]
    public class UParticleSystem : UObject
    {
        [PropertyField]
        public float UpdateTime_Delta { get; set; }

        [PropertyField]
        public UArray<FObject> Emitters { get; set; } // ParticleEmitter

        [PropertyField]
        public bool bUseFixedRelativeBoundingBox { get; set; }

        [PropertyField]
        public bool bShouldResetPeakCounts { get; set; }

        [PropertyField]
        public UArray<float> LODDistances { get; set; }

        [PropertyField]
        public UArray<FParticleSystemLOD> LODSettings { get; set; }

        [PropertyField]
        public FBox FixedRelativeBoundingBox { get; set; }
    }

    [UnrealStruct("ParticleSystemLOD")]
    public class FParticleSystemLOD
    {
        [StructField]
        public bool bLit { get; set; }

        [StructField]
        public bool bIsObscureLOD { get; set; }
    }

    [UnrealClass("ParticleEmitter")]
    public class UParticleEmitter : UObject
    {
        [PropertyField]
        public UArray<FObject> LODLevels { get; set; } // ParticleLODLevel
    }

    [UnrealClass("ParticleSpriteEmitter")]
    public class UParticleSpriteEmitter : UParticleEmitter
    {
    }

    public enum EParticleBurstMethod
    {
        EPBM_Instant = 0,
        EPBM_Interpolated = 1,
        EPBM_MAX = 2
    }

    [UnrealClass("ParticleModule")]
    public class UParticleModule : UObject
    {
        [PropertyField]
        public byte LODValidity { get; set; }
    }

    [UnrealClass("ParticleModuleSpawnBase")]
    public class UParticleModuleSpawnBase : UParticleModule
    {
    }

    [UnrealClass("ParticleModuleSpawn")]
    public class UParticleModuleSpawn : UParticleModuleSpawnBase
    {
        [PropertyField]
        public FRawDistributionFloat Rate { get; set; }

        [PropertyField]
        public FRawDistributionFloat RateScale { get; set; }

        [PropertyField]
        public EParticleBurstMethod ParticleBurstMethod { get; set; }

        [PropertyField]
        public UArray<FParticleBurst> BurstList { get; set; }
    }

    [UnrealClass("ParticleModuleColorBase")]
    public class UParticleModuleColorBase : UParticleModule
    {
    }

    [UnrealClass("ParticleModuleColorOverLife")]
    public class UParticleModuleColorOverLife : UParticleModuleColorBase
    {
        [PropertyField]
        public FRawDistributionVector ColorOverLife { get; set; }

        [PropertyField]
        public FRawDistributionFloat AlphaOverLife { get; set; }
    }


    [UnrealStruct("ParticleBurst")]
    public class FParticleBurst
    {
        [StructField]
        public int Count { get; set; }

        [StructField]
        public int CountLow { get; set; }

        [StructField]
        public float Time { get; set; }
    }

    [UnrealClass("ParticleLODLevel")]
    public class UParticleLODLevel : UObject
    {
        [PropertyField]
        public int Level { get; set; }

        [PropertyField]
        public FObject RequiredModule { get; set; } // ParticleModuleRequired

        [PropertyField]
        public UArray<FObject> Modules { get; set; } // ParticleModule

        [PropertyField]
        public FObject TypeDataModule { get; set; } // ParticleModule

        [PropertyField]
        public FObject SpawnModule { get; set; } // ParticleModuleSpawn

        [PropertyField]
        public int PeakActiveParticles { get; set; }
    }

    // ParticleSystemComponent — placed in level as a child of an emitter actor
    // (or top-level). The 3D viewer reads Translation/Rotation/Scale to position
    // a sprite billboard at the world location; Template tells us which
    // ParticleSystem (and by name, what visual flavor — fire, glow, etc).
    [UnrealClass("ParticleSystemComponent")]
    public class UParticleSystemComponent : UObject
    {
        [PropertyField] public FVector  Translation { get; set; }
        [PropertyField] public FRotator Rotation    { get; set; }
        [PropertyField] public float    Scale       { get; set; } = 1.0f;
        [PropertyField] public FVector  Scale3D     { get; set; }
        [PropertyField] public FObject  Template    { get; set; } // ParticleSystem
        // Match the SMC override: skip the cooked TemplateOwnerClass +
        // TemplateName prefix and consume the 4-byte 0xFFFFFFFF marker so the
        // tagged property reader starts at the right offset.
        public override void ReadBuffer(UBuffer buffer)
        {
            int net = buffer.Reader.ReadInt32();
            typeof(UObject).GetProperty("NetIndex")?.SetValue(this, net);
            // Peek for the cooked-content 0xFFFFFFFF marker — present on
            // standalone SMCs/PSCs in cooked Marvel content but absent on
            // some PSC exports (different cooker pass).
            int saved = buffer.Reader.CurrentOffset;
            int peek = buffer.Reader.ReadInt32();
            if (peek != -1) buffer.Reader.Seek(saved);
            if (!buffer.IsAbstractClass)
            {
                var readProps = typeof(UObject).GetMethod("ReadProperties",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var props = readProps?.Invoke(this, new object[] { buffer });
                var setProps = typeof(UObject).GetMethod("SetProperties",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (props != null && setProps != null) setProps.Invoke(this, new object[] { props });
            }
        }
    }

    // Emitter actor — outer wrapper for a ParticleSystemComponent.
    [UnrealClass("Emitter")]
    public class UEmitter : Engine.UActor
    {
        [PropertyField] public FObject ParticleSystemComponent { get; set; }
    }
}
