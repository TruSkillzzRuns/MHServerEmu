using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine
{
    [UnrealClass("Actor")]
    public class UActor : UObject
    {
        [PropertyField]
        public UArray<FObject> Components { get; set; } // ActorComponent

        [PropertyField]
        public FObject CollisionComponent { get; set; } // PrimitiveComponent

        // World placement — written by the editor as tagged properties on the
        // actor itself. SMCs/SkelMCs attached to the actor inherit this when
        // their own Translation is zero.
        [PropertyField] public FVector  Location    { get; set; }
        [PropertyField] public FRotator Rotation    { get; set; }
        [PropertyField] public float    DrawScale   { get; set; } = 1.0f;
        [PropertyField] public FVector  DrawScale3D { get; set; }
    }

    [UnrealClass("ActorComponent")]
    public class UActorComponent : UComponent
    {

    }

    [UnrealClass("PrimitiveComponent")]
    public class UPrimitiveComponent : UActorComponent
    {

    }

    [UnrealClass("CylinderComponent")]
    public class UCylinderComponent : UPrimitiveComponent
    {

    }

    // ─── Level + placed actors ───────────────────────────────────────────
    // The 3D viewer's "per-instance placement" path needs ULevel.Actors[]
    // walking. AStaticMeshActor holds a StaticMeshComponent ObjectProperty
    // that pulls the per-instance transform from the SMC's Translation/etc
    // (or falls back to the actor's Location).

    [UnrealClass("StaticMeshActorBase")]
    public class UStaticMeshActorBase : UActor
    {
        [PropertyField]
        public FObject StaticMeshComponent { get; set; } // UStaticMeshComponent
    }

    [UnrealClass("StaticMeshActor")]
    public class UStaticMeshActor : UStaticMeshActorBase
    {
    }

    [UnrealClass("DynamicSMActor")]
    public class UDynamicSMActor : UActor
    {
        [PropertyField]
        public FObject StaticMeshComponent { get; set; }
    }

    [UnrealClass("InterpActor")]
    public class UInterpActor : UDynamicSMActor
    {
    }

    // NOTE: ULevel has class-specific TTransArray<AActor*> serialization
    // after the property block that doesn't match a straight ReadArray, so
    // we deliberately don't register a ULevel class here. The level-package
    // walker iterates ExportTable entries by class name instead — every
    // actor is its own export in the .upk, so a class filter on
    // {StaticMeshActor, DynamicSMActor, InterpActor, ...} is enough.
}
