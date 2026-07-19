// OmegaDev2 "Region Events" tool — write/clone half of the runtime
// prototype field editor. THIS IS THE ACTUAL MUTATION SURFACE: every write
// here is a reflection-based field set on an already-loaded, globally
// cached Prototype instance — visible to every future caller referencing
// that PrototypeId for the rest of the server session, with no revert path
// short of a restart. See RuntimePrototypeEditor's class doc for the full
// rationale (no new PrototypeId can ever be minted; this repurposes an
// existing one in place). No .sip/client file is ever touched.
//
//   POST /webapi/protoeditor/write  { protoRef, path, valueType, value } -> { ok, previousValue, newValue }
//   POST /webapi/protoeditor/clone  { sourceProtoRef, targetProtoRef, path, targetPath? } -> { ok, previousValue, newValue }

using System.Text.Json;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.PatchManager;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class PrototypeEditorWriteWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();

            string protoRefStr = null, path = null, valueTypeStr = null;
            JsonElement valueElement = default;
            bool hasValue = false;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("protoRef", out var pr)) protoRefStr = pr.GetString();
                if (root.TryGetProperty("path", out var p)) path = p.GetString();
                if (root.TryGetProperty("valueType", out var vt)) valueTypeStr = vt.GetString();
                if (root.TryGetProperty("value", out var v)) { valueElement = v.Clone(); hasValue = true; }
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            PrototypeId protoRef = (PrototypeId)PhantomsWebUtil.ParseRef(protoRefStr);
            if (protoRef == PrototypeId.Invalid || string.IsNullOrWhiteSpace(path) || hasValue == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "protoRef, path, and value are required" });
                return;
            }

            if (Enum.TryParse<MHServerEmu.Games.GameData.PatchManager.ValueType>(valueTypeStr, out var valueType) == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"unknown valueType '{valueTypeStr}'" });
                return;
            }

            ValueBase newValue;
            try
            {
                newValue = PatchEntryConverter.GetValueBase(valueElement, valueType);
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"failed to parse value: {ex.Message}" });
                return;
            }

            if (RuntimePrototypeEditor.TryWriteField(protoRef, path, newValue, out object previousValue, out string error) == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error });
                return;
            }

            await context.SendJsonAsync(new
            {
                Ok = true,
                ProtoRef = protoRefStr,
                Path = path,
                PreviousValue = previousValue?.ToString(),
                NewValue = newValue.GetValue()?.ToString(),
            });
        }
    }

    public class PrototypeEditorCloneWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();

            string sourceProtoRefStr = null, targetProtoRefStr = null, path = null, targetPath = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("sourceProtoRef", out var sp)) sourceProtoRefStr = sp.GetString();
                if (root.TryGetProperty("targetProtoRef", out var tp)) targetProtoRefStr = tp.GetString();
                if (root.TryGetProperty("path", out var p)) path = p.GetString();
                if (root.TryGetProperty("targetPath", out var tgp)) targetPath = tgp.GetString();
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            PrototypeId sourceProtoRef = (PrototypeId)PhantomsWebUtil.ParseRef(sourceProtoRefStr);
            PrototypeId targetProtoRef = (PrototypeId)PhantomsWebUtil.ParseRef(targetProtoRefStr);
            if (sourceProtoRef == PrototypeId.Invalid || targetProtoRef == PrototypeId.Invalid || string.IsNullOrWhiteSpace(path))
            {
                await context.SendJsonAsync(new { Ok = false, Error = "sourceProtoRef, targetProtoRef, and path are required" });
                return;
            }

            if (RuntimePrototypeEditor.TryCloneField(sourceProtoRef, targetProtoRef, path, targetPath, out object previousValue, out string error) == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error });
                return;
            }

            await context.SendJsonAsync(new
            {
                Ok = true,
                SourceProtoRef = sourceProtoRefStr,
                TargetProtoRef = targetProtoRefStr,
                Path = path,
                TargetPath = targetPath ?? path,
                PreviousValue = previousValue?.ToString(),
            });
        }
    }
}
