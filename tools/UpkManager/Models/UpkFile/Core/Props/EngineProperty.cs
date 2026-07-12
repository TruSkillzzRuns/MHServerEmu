using System;
using System.Collections.Generic;
using System.Reflection;

using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Core
{
    public class EngineProperty : UProperty
    {
        public Type StructType { get; private set; }
        public string StructName { get; private set; }
        public List<UnrealProperty> Fields { get; set; } = [];
        public ResultProperty Result { get; private set; }
        public int RemainingData { get; private set; }
        public bool IsAtomic { get; private set; }
        public IAtomicStruct AtomicValue { get; private set; }

        public override string ToString() => StructName;

        public EngineProperty(Type type)
        {
            StructType = type;
            IsAtomic = CheckIfAtomic(type);

            if (IsAtomic)
            {
                var atomicAttr = type.GetCustomAttribute<AtomicStructAttribute>();
                StructName = atomicAttr.Name;
            }
            else
            {
                var unrealAttr = type.GetCustomAttribute<UnrealStructAttribute>();
                StructName = unrealAttr?.StructName ?? type.Name;
            }
        }

        private static bool CheckIfAtomic(Type type)
        {
            var atomicAttr = type.GetCustomAttribute<AtomicStructAttribute>();
            if (atomicAttr != null) return atomicAttr.IsAtomicProperty;

            return false;
        }

        public override void BuildVirtualTree(VirtualNode valueTree)
        {
            if (IsAtomic)
            {
                valueTree.Children.Add(new(AtomicValue.Format));
            }
            else
            {
                foreach (var prop in Fields)
                    valueTree.Children.Add(prop.VirtualTree);
            }

            if (Result != ResultProperty.None || RemainingData > 0)
                valueTree.Children.Add(new($"Data [{Result}][{RemainingData}]"));
        }

        public override void ReadPropertyValue(UBuffer buffer, int size, UnrealProperty property)
        {
            int offset = buffer.Reader.CurrentOffset;

            if (IsAtomic)
                ReadAtomicValue(buffer);
            else
                ReadUnrealStructValue(buffer, size, property, offset);

            RemainingData = size - (buffer.Reader.CurrentOffset - offset);
        }


        private void ReadAtomicValue(UBuffer buffer)
        {
            try
            {
                var readData = StructType.GetMethod("ReadData", BindingFlags.Public | BindingFlags.Static, null, [typeof(UBuffer)], null);
                if (readData != null)
                    AtomicValue = (IAtomicStruct)readData.Invoke(null, new object[] { buffer });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error reading atomic struct {StructName}: {ex.Message}");
                Result = ResultProperty.Error;
            }
        }

        private void ReadUnrealStructValue(UBuffer buffer, int size, UnrealProperty property, int offset)
        {
            Fields.Clear();
            Result = ResultProperty.Success;

            do
            {
                var prop = new UnrealProperty();
                try
                {
                    Result = prop.ReadProperty(buffer, Parent);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error reading property: {ex.Message}");
                    Result = ResultProperty.Error;
                    return;
                }

                if (Result != ResultProperty.Success) break;
                Fields.Add(prop);
            }
            while (Result == ResultProperty.Success);
        }

    }
}
