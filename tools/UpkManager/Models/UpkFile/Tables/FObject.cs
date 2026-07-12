using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Objects;

namespace UpkManager.Models.UpkFile.Tables
{
    public class FObject(UnrealObjectTableEntryBase tableEntry) : FName
    {
        public UObject Object { get; private set; }
        public UnrealObjectTableEntryBase TableEntry { get; set; } = tableEntry;

        public string GetPathName()
        {
            try
            {
                return TableEntry?.GetPathName() ?? string.Empty;
            }
            catch
            {
                return Name ?? string.Empty;
            }
        }

        public T LoadObject<T>() where T : UObject
        {
            if (Object is T cached)
                return cached;

            try
            {
                var entry = TableEntry;

                if (entry is UnrealImportTableEntry import)
                {
                    entry = import.GetExportEntry();
                }

                if (entry is UnrealExportTableEntry export)
                {
                    if (export.UnrealObject == null)
                        export.UnrealHeader?.ReadExportObjectAsync(export, null).GetAwaiter().GetResult();

                    if (export.UnrealObject == null)
                        export.ParseUnrealObject(false, false).GetAwaiter().GetResult();

                    if (export.UnrealObject is IUnrealObject uObject)
                    {
                        Object = uObject.UObject as T;
                        return Object as T;
                    }
                }
            }
            catch
            {
                return default;
            }

            return default;
        }
    }
}
