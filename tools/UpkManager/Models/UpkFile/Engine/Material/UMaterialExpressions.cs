using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.Material
{
    // Minimal UE3 MaterialExpression models. Only fields the emissive-tint
    // evaluator cares about are declared — reflection-based deserialization
    // will simply skip properties we haven't modelled.
    //
    // NOTE: The "FExpressionInput" struct in UE3 is binary-identical to
    // FMaterialInput (Expression + OutputIndex + Mask + GCC64_Padding). We
    // reuse FMaterialInput for simplicity since both deserialize the same
    // property tags.

    [UnrealStruct("ExpressionInput")]
    public class FExpressionInput : FMaterialInput
    {
    }

    [UnrealClass("MaterialExpression")]
    public class UMaterialExpression : UObject
    {
        [PropertyField]
        public int MaterialExpressionEditorX { get; set; }

        [PropertyField]
        public int MaterialExpressionEditorY { get; set; }

        [PropertyField]
        public FGuid ExpressionGUID { get; set; }
    }

    [UnrealClass("MaterialExpressionConstant")]
    public class UMaterialExpressionConstant : UMaterialExpression
    {
        [PropertyField]
        public float R { get; set; }
    }

    [UnrealClass("MaterialExpressionConstant2Vector")]
    public class UMaterialExpressionConstant2Vector : UMaterialExpression
    {
        [PropertyField]
        public float R { get; set; }

        [PropertyField]
        public float G { get; set; }
    }

    [UnrealClass("MaterialExpressionConstant3Vector")]
    public class UMaterialExpressionConstant3Vector : UMaterialExpression
    {
        [PropertyField]
        public float R { get; set; }

        [PropertyField]
        public float G { get; set; }

        [PropertyField]
        public float B { get; set; }
    }

    [UnrealClass("MaterialExpressionConstant4Vector")]
    public class UMaterialExpressionConstant4Vector : UMaterialExpression
    {
        [PropertyField]
        public float R { get; set; }

        [PropertyField]
        public float G { get; set; }

        [PropertyField]
        public float B { get; set; }

        [PropertyField]
        public float A { get; set; }
    }

    [UnrealClass("MaterialExpressionMultiply")]
    public class UMaterialExpressionMultiply : UMaterialExpression
    {
        [PropertyField]
        public FExpressionInput A { get; set; }

        [PropertyField]
        public FExpressionInput B { get; set; }
    }

    [UnrealClass("MaterialExpressionAdd")]
    public class UMaterialExpressionAdd : UMaterialExpression
    {
        [PropertyField]
        public FExpressionInput A { get; set; }

        [PropertyField]
        public FExpressionInput B { get; set; }
    }

    [UnrealClass("MaterialExpressionSubtract")]
    public class UMaterialExpressionSubtract : UMaterialExpression
    {
        [PropertyField]
        public FExpressionInput A { get; set; }

        [PropertyField]
        public FExpressionInput B { get; set; }
    }

    [UnrealClass("MaterialExpressionDivide")]
    public class UMaterialExpressionDivide : UMaterialExpression
    {
        [PropertyField]
        public FExpressionInput A { get; set; }

        [PropertyField]
        public FExpressionInput B { get; set; }
    }

    [UnrealClass("MaterialExpressionLinearInterpolate")]
    public class UMaterialExpressionLinearInterpolate : UMaterialExpression
    {
        [PropertyField]
        public FExpressionInput A { get; set; }

        [PropertyField]
        public FExpressionInput B { get; set; }

        [PropertyField]
        public FExpressionInput Alpha { get; set; }
    }

    [UnrealClass("MaterialExpressionPower")]
    public class UMaterialExpressionPower : UMaterialExpression
    {
        [PropertyField]
        public FExpressionInput Base { get; set; }

        [PropertyField]
        public FExpressionInput Exponent { get; set; }
    }

    [UnrealClass("MaterialExpressionClamp")]
    public class UMaterialExpressionClamp : UMaterialExpression
    {
        [PropertyField]
        public FExpressionInput Input { get; set; }

        [PropertyField]
        public FExpressionInput Min { get; set; }

        [PropertyField]
        public FExpressionInput Max { get; set; }

        [PropertyField]
        public float MinDefault { get; set; }

        [PropertyField]
        public float MaxDefault { get; set; }
    }

    [UnrealClass("MaterialExpressionComponentMask")]
    public class UMaterialExpressionComponentMask : UMaterialExpression
    {
        [PropertyField]
        public FExpressionInput Input { get; set; }

        [PropertyField]
        public bool R { get; set; }

        [PropertyField]
        public bool G { get; set; }

        [PropertyField]
        public bool B { get; set; }

        [PropertyField]
        public bool A { get; set; }
    }

    [UnrealClass("MaterialExpressionAppendVector")]
    public class UMaterialExpressionAppendVector : UMaterialExpression
    {
        [PropertyField]
        public FExpressionInput A { get; set; }

        [PropertyField]
        public FExpressionInput B { get; set; }
    }

    [UnrealClass("MaterialExpressionVectorParameter")]
    public class UMaterialExpressionVectorParameter : UMaterialExpression
    {
        [PropertyField]
        public FName ParameterName { get; set; }

        [PropertyField]
        public FLinearColor DefaultValue { get; set; }
    }

    [UnrealClass("MaterialExpressionScalarParameter")]
    public class UMaterialExpressionScalarParameter : UMaterialExpression
    {
        [PropertyField]
        public FName ParameterName { get; set; }

        [PropertyField]
        public float DefaultValue { get; set; }
    }

    [UnrealClass("MaterialExpressionTextureSample")]
    public class UMaterialExpressionTextureSample : UMaterialExpression
    {
        [PropertyField]
        public FObject Texture { get; set; }
    }

    [UnrealClass("MaterialExpressionTextureSampleParameter")]
    public class UMaterialExpressionTextureSampleParameter : UMaterialExpressionTextureSample
    {
        [PropertyField]
        public FName ParameterName { get; set; }
    }

    [UnrealClass("MaterialExpressionTextureSampleParameter2D")]
    public class UMaterialExpressionTextureSampleParameter2D : UMaterialExpressionTextureSampleParameter
    {
    }

    [UnrealClass("MaterialExpressionTextureSampleParameterCube")]
    public class UMaterialExpressionTextureSampleParameterCube : UMaterialExpressionTextureSampleParameter
    {
    }

    [UnrealClass("MaterialExpressionTextureSampleParameterMovie")]
    public class UMaterialExpressionTextureSampleParameterMovie : UMaterialExpressionTextureSampleParameter
    {
    }

    [UnrealClass("MaterialExpressionTextureSampleParameterSubUV")]
    public class UMaterialExpressionTextureSampleParameterSubUV : UMaterialExpressionTextureSampleParameter2D
    {
    }
}
