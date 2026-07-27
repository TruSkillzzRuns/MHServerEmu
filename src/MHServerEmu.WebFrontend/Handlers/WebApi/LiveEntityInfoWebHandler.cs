// Debug endpoint — GET /webapi/debug/liveentity?player=&entityId=0x...
// Reports LIVE runtime state (Cell/Region/IsInWorld/AOI-relevant flags) for
// an already-spawned entity, as opposed to /webapi/debug/visiblebydefault
// which only reads static prototype data. Used to test whether a
// runtime-CreateEntity-spawned prop actually resolves a Cell the same way a
// spawned Agent does. Read-only, no state changes.

using System.Linq;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class LiveEntityInfoWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            string entityIdStr = PhantomsWebUtil.QueryParam(context, "entityId");
            if (ulong.TryParse(entityIdStr?.TrimStart('0', 'x', 'X'), System.Globalization.NumberStyles.HexNumber, null, out ulong entityId) == false
                && ulong.TryParse(entityIdStr, out entityId) == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing or invalid 'entityId' (hex or decimal)" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                var entity = p.Game.EntityManager.GetEntity<WorldEntity>(entityId);
                if (entity == null)
                    return new { Ok = false, Error = "entity not found (destroyed or never existed on this Game instance)" };

                return new
                {
                    Ok = true,
                    EntityId = $"0x{entityId:X}",
                    PrototypeName = GameDatabase.GetPrototypeName(entity.PrototypeDataRef),
                    entity.IsInWorld,
                    entity.IsDestroyed,
                    IsInGame = entity.Game != null,
                    RegionId = entity.Region?.Id ?? 0,
                    RegionName = entity.Region != null ? GameDatabase.GetPrototypeName(entity.Region.PrototypeDataRef) : "<null>",
                    CellId = entity.Cell?.Id ?? 0,
                    AreaId = entity.Cell?.Area?.Id ?? 0,
                    Position = new { entity.RegionLocation.Position.X, entity.RegionLocation.Position.Y, entity.RegionLocation.Position.Z },
                    entity.IsLiveTuningVisible,
                    VisibilityStatus = GameDatabase.InteractionManager.GetVisibilityStatus(p, entity),
                    entity.IsCloneParent,
                    RestrictedToPlayerGuid = (ulong)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.RestrictedToPlayerGuid],
                    RestrictedToPlayerGuidParty = (ulong)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.RestrictedToPlayerGuidParty],
                    OwnerPlayerDbId = p.DatabaseUniqueId,
                    UnrealClass = entity.WorldEntityPrototype != null
                        ? GameDatabase.GetAssetName(entity.WorldEntityPrototype.UnrealClass)
                        : "<null WorldEntityPrototype>",
                    UnrealClassAssetId = entity.WorldEntityPrototype != null ? (ulong)entity.WorldEntityPrototype.UnrealClass : 0,
                    Health = (long)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.Health],
                    HealthMax = (long)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.HealthMax],
                    entity.IsDead,
                    Stealth = (int)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.Stealth],
                    StealthDetection = (int)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.StealthDetection],
                    Untargetable = (bool)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.Untargetable],
                    Unaffectable = (bool)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.Unaffectable],
                    Invulnerable = (bool)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.Invulnerable],
                    Dormant = (bool)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.Dormant],
                    Visible = (bool)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.Visible],
                    IsVisibleWhenDormant = (entity as MHServerEmu.Games.Entities.Agent)?.IsVisibleWhenDormant,
                    DramaticEntrancePlayedOnce = (bool)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.DramaticEntrancePlayedOnce],
                    Rank = entity.Properties.HasProperty(MHServerEmu.Games.Properties.PropertyEnum.Rank)
                        ? GameDatabase.GetPrototypeName((MHServerEmu.Games.GameData.PrototypeId)entity.Properties[MHServerEmu.Games.Properties.PropertyEnum.Rank])
                        : "<none>",
                    EnemyBoosts = entity.Properties.IteratePropertyRange(MHServerEmu.Games.Properties.PropertyEnum.EnemyBoost)
                        .Select(kvp => { MHServerEmu.Games.GameData.PrototypeId boostRef = default; MHServerEmu.Games.Properties.Property.FromParam(kvp.Key, 0, out boostRef); return GameDatabase.GetPrototypeName(boostRef); })
                        .ToList(),
                    Bounds = new
                    {
                        entity.Bounds.Geometry,
                        entity.Bounds.CollisionType,
                        Radius = entity.Bounds.Radius,
                        HalfHeight = entity.Bounds.HalfHeight,
                    },
                };
            });
            await context.SendJsonAsync(result);
        }
    }
}
