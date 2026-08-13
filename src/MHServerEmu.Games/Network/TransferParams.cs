using Gazillion;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.MetaGames;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Network
{
    /// <summary>
    /// Contains data needed to put a player into a region.
    /// </summary>
    public class TransferParams
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public PlayerConnection PlayerConnection { get; }

        public ulong DestRegionId { get; set; }
        public PrototypeId DestRegionProtoRef { get; set; }

        public NetStructRegionLocation DestLocation { get; private set; }
        public NetStructRegionTarget DestTarget { get; private set; }
        public ulong DestEntityDbId { get; set; }

        public int DestTeamIndex { get; set; }

        // TODO
        // bool HasInvite
        // NetStructRegionOrigin Origin

        public TransferParams(PlayerConnection playerConnection)
        {
            PlayerConnection = playerConnection;
        }

        public void SetFromProtobuf(NetStructTransferParams transferParams)
        {
            DestRegionId = transferParams.DestRegionId;
            DestRegionProtoRef = (PrototypeId)transferParams.DestRegionProtoId;

            DestLocation = transferParams.HasDestLocation ? transferParams.DestLocation : null;
            DestTarget = transferParams.HasDestTarget ? transferParams.DestTarget : null;
            DestEntityDbId = transferParams.HasDestEntityDbId ? transferParams.DestEntityDbId : 0;

            DestTeamIndex = transferParams.HasDestTeamIndex ? transferParams.DestTeamIndex : -1;
        }
        
        public bool FindStartLocation(out Vector3 position, out Orientation orientation)
        {
            position = Vector3.Zero;
            orientation = Orientation.Zero;

            Game game = PlayerConnection.Game;

            Region region = game.RegionManager.GetRegion(DestRegionId);
            if (!Verify.IsNotNull(region)) return false;

            Area startArea = region.GetStartArea();
            if (!Verify.IsNotNull(startArea)) return false;

            // Check if there is a pvp team
            if (FindStartLocationFromPvPTeam(region, ref position, ref orientation))
                return true;

            // Check if there is a region-specific override (e.g. divided start targets)
            if (FindStartLocationFromRegionOverride(region, ref position, ref orientation))
                return true;

            // Check if we have an entity to teleport to (e.g. another player)
            if (FindStartLocationFromEntityDbId(region, ref position, ref orientation))
                return true;

            // Try specific location (e.g. returning from town using bodyslider)
            if (FindStartLocationFromSpecificLocation(region, ref position, ref orientation))
                return true;

            // Try to use the provided target
            if (FindStartLocationFromTarget(region, ref position, ref orientation))
                return true;

            // Fall back to the start target for the region
            if (FindStartLocationFromRegionStartTarget(region, ref position, ref orientation))
                return true;

            // Last resort. Documented as "should never really happen", but
            // AbandonedAIMRegion reaches here on EVERY entry, so it has to
            // produce a genuinely usable spot rather than a nominal one.
            //
            // The original fallback used the first cell's geometric centre.
            // For that region the start area has a SINGLE cell spanning 2304
            // units and centred on the origin, so the fallback was (0,0,0) -
            // inside a wall. The player spawned embedded in scenery and
            // movement powers failed with TargetPositionInvalid.
            //
            // Avatar.AdjustStartPositionIfNeeded can't rescue that: it only
            // probes a 64-unit radius, which is nothing against an area that
            // size. Search the whole area instead, straight through
            // ChooseRandomPositionNearPoint with a radius derived from the
            // area's own bounds.
            position = startArea.Cells.First().Value.RegionBounds.Center;

            Bounds bounds = new();
            bounds.InitializeCapsule(64f, 128f, BoundsCollisionType.Blocking, BoundsFlags.None);
            bounds.Center = position;

            // Half the diagonal of the area's footprint covers every corner of
            // it from the centre, whatever the shape.
            float searchRadius = MathF.Max(startArea.RegionBounds.Width, startArea.RegionBounds.Length);

            if (region.ChooseRandomPositionNearPoint(ref bounds, Navi.PathFlags.Walk,
                    PositionCheckFlags.CanBeBlockedEntity,
                    BlockingCheckFlags.CheckGroundMovementPowers | BlockingCheckFlags.CheckLanding,
                    0f, searchRadius, out Vector3 walkable))
            {
                position = walkable;
            }
            else
            {
                Logger.Warn($"FindStartLocation(): No walkable position within {searchRadius} of " +
                            $"[{position}] in [{region}]; using it as-is.");
            }

            Verify.IsTrue(false, LoggingLevel.Error, $"Failed to find valid start location! region=[{region}], fallbackPosition=[{position}]");
            return true;
        }

        private bool FindStartLocationFromPvPTeam(Region region, ref Vector3 position, ref Orientation orientation)
        {
            if (region.MetaGames.Count == 0)
                return false;

            Game game = PlayerConnection.Game;
            EntityManager entityManager = game.EntityManager;
            Player player = PlayerConnection.Player;

            PvPTeam pvpTeam = null;
            foreach (ulong metaGameId in region.MetaGames)
            {
                PvP pvp = entityManager.GetEntity<PvP>(metaGameId);
                if (pvp == null) return false;

                pvpTeam = pvp.GetTeamForPlayer(player) as PvPTeam;
                if (pvpTeam == null) return false;
                break;
            }

            PrototypeId startTarget = pvpTeam.StartTarget;
            if (startTarget == PrototypeId.Invalid)
                return false;

            RegionConnectionTargetPrototype targetProto = startTarget.As<RegionConnectionTargetPrototype>();
            if (!Verify.IsNotNull(targetProto)) return false;

            bool isEquivalent = RegionPrototype.Equivalent(targetProto.Region.As<RegionPrototype>(), region.Prototype);
            if (!Verify.IsTrue(isEquivalent, $"Target region mismatch, expected {region.PrototypeDataRef.GetName()}, got {targetProto.Region.GetName()}"))
                return false;

            PrototypeId areaProtoRef = targetProto.Area;
            PrototypeId cellProtoRef = GameDatabase.GetDataRefByAsset(targetProto.Cell);
            PrototypeId entityProtoRef = targetProto.Entity;

            bool found = region.FindTargetLocation(ref position, ref orientation, areaProtoRef, cellProtoRef, entityProtoRef);
            if (!Verify.IsTrue(found, $"Failed to find location for target {targetProto}"))
                return false;

            return true;
        }

        private bool FindStartLocationFromRegionOverride(Region region, ref Vector3 position, ref Orientation orientation)
        {
            if (region.Prototype.Behavior != RegionBehavior.MatchPlay)
                return false;

            Player player = PlayerConnection.Player;
            PrototypeId startTarget = region.GetStartTarget(player);
            if (startTarget == PrototypeId.Invalid)
                return false;

            RegionConnectionTargetPrototype targetProto = startTarget.As<RegionConnectionTargetPrototype>();
            if (!Verify.IsNotNull(targetProto)) return false;

            bool isEquivalent = RegionPrototype.Equivalent(targetProto.Region.As<RegionPrototype>(), region.Prototype);
            if (!Verify.IsTrue(isEquivalent, $"Target region mismatch, expected {region.PrototypeDataRef.GetName()}, got {targetProto.Region.GetName()}"))
                return false;

            PrototypeId areaProtoRef = targetProto.Area;
            PrototypeId cellProtoRef = GameDatabase.GetDataRefByAsset(targetProto.Cell);
            PrototypeId entityProtoRef = targetProto.Entity;

            bool found = region.FindTargetLocation(ref position, ref orientation, areaProtoRef, cellProtoRef, entityProtoRef);
            if (!Verify.IsTrue(found, $"Failed to find location for target {targetProto}"))
                return false;

            return true;
        }

        private bool FindStartLocationFromEntityDbId(Region region, ref Vector3 position, ref Orientation orientation)
        {
            if (DestEntityDbId == 0)
                return false;

            Entity entity = PlayerConnection?.Game.EntityManager.GetEntityByDbGuid<Entity>(DestEntityDbId);
            if (entity == null)
                return false;

            WorldEntity worldEntity = entity is Player player ? player.CurrentAvatar : entity as WorldEntity;
            if (worldEntity == null)
                return false;

            Vector3 entityPosition = worldEntity.ExitWorldRegionLocation.Position;
            if (Avatar.AdjustStartPositionIfNeeded(region, ref entityPosition) == false)
                return false;

            position = entityPosition;
            return true;
        }

        private bool FindStartLocationFromSpecificLocation(Region region, ref Vector3 position, ref Orientation orientation)
        {
            if (DestLocation == null)
                return false;

            ulong regionId = DestLocation.RegionId;
            Vector3 destPosition = new(DestLocation.Position);

            if (!Verify.IsTrue(regionId == region.Id && Vector3.IsNearZero(destPosition) == false, $"Invalid location provided\n{DestLocation}"))
                return false;

            Avatar.AdjustStartPositionIfNeeded(region, ref destPosition);
            position = destPosition;
            return true;
        }

        private bool FindStartLocationFromTarget(Region region, ref Vector3 position, ref Orientation orientation)
        {
            if (DestTarget == null)
                return false;

            PrototypeId regionProtoRef = (PrototypeId)DestTarget.RegionProtoId;
            PrototypeId areaProtoRef = (PrototypeId)DestTarget.AreaProtoId;
            PrototypeId cellProtoRef = (PrototypeId)DestTarget.CellProtoId;
            PrototypeId entityProtoRef = (PrototypeId)DestTarget.EntityProtoId;

            bool isEquivalent = RegionPrototype.Equivalent(regionProtoRef.As<RegionPrototype>(), region.Prototype);
            if (!Verify.IsTrue(isEquivalent, $"Target region mismatch, expected {region.PrototypeDataRef.GetName()}, got {regionProtoRef.GetName()}"))
                return false;

            if (region.FindTargetLocation(ref position, ref orientation, areaProtoRef, cellProtoRef, entityProtoRef) == false)
                return false;

            // Check for collisions and try to adjust position so that avatars don't overlap in one point.
            Avatar.AdjustStartPositionIfNeeded(region, ref position, true);
            return true;
        }

        private bool FindStartLocationFromRegionStartTarget(Region region, ref Vector3 position, ref Orientation orientation)
        {
            // This is valid and can happen in the cases where the entrance portal entity is destroyed (e.g. returning from a Danger Room scenario).
            var targetProto = region.Prototype.StartTarget.As<RegionConnectionTargetPrototype>();
            if (targetProto == null)
                return false;

            DestTarget = NetStructRegionTarget.CreateBuilder()
                .SetRegionProtoId((ulong)region.PrototypeDataRef)     // Keep this within the same region, we are just falling back to a different position.
                .SetAreaProtoId((ulong)targetProto.Area)
                .SetCellProtoId((ulong)GameDatabase.GetDataRefByAsset(targetProto.Cell))
                .SetEntityProtoId((ulong)targetProto.Entity)
                .Build();

            return FindStartLocationFromTarget(region, ref position, ref orientation);
        }
    }
}
