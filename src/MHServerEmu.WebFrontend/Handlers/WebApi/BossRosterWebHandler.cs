// OmegaDev2 "Enemy Phantoms" page — Boss Roster panel. Spawns a REAL
// boss-tier AgentPrototype (Doom/Kraven/Green Goblin-class content) as a
// plain hostile Agent near the player — NOT a synthetic Player+Avatar
// phantom hero (that's what /webapi/arena/enemyphantoms/spawn does).
// Mirrors EntityCommands.cs's "entity create" command
// (CommandHelper.FindPrototype + EntityHelper.CreateAgent), just reachable
// over HTTP with a resolved ProtoRef instead of a fuzzy name match.
//
//   GET  /webapi/bossroster/catalog                 -> every real boss the game ships (same filter Player.WaveDirector.cs's GetEndlessBossPool uses)
//   POST /webapi/bossroster/spawn   { playerName, bossRef, count }
//   POST /webapi/bossroster/clear   { playerName }   -> despawn every boss THIS handler has spawned for that player

using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class BossRosterCatalogWebHandler : WebHandler
    {
        private static object _cached;
        private static readonly object _gate = new();

        protected override async Task Get(WebRequestContext context)
        {
            object payload = _cached;
            if (payload == null)
            {
                lock (_gate)
                {
                    if (_cached == null) _cached = Build();
                    payload = _cached;
                }
            }
            await context.SendJsonAsync(payload);
        }

        private static object Build()
        {
            var entries = new List<BossCatalogEntry>(256);
            foreach (PrototypeId agentRef in DataDirectory.Instance.IteratePrototypesInHierarchy<AgentPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                if (agentRef == PrototypeId.Invalid) continue;
                var proto = agentRef.As<AgentPrototype>();
                if (proto == null || proto is AvatarPrototype) continue;

#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                AssetId iconAssetId = proto.IconPathHiRes != 0 ? proto.IconPathHiRes : proto.IconPath;
#else
                AssetId iconAssetId = proto.IconPath;
#endif
                if (iconAssetId == 0) continue;

                string path = GameDatabase.GetPrototypeName(agentRef);
                if (string.IsNullOrEmpty(path)) continue;
                if (path.IndexOf("/Bosses/", StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (path.IndexOf("/Test/", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("/Tests/", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("/Debug/", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("/zzzDeprecated", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("/Cinematic", StringComparison.OrdinalIgnoreCase) >= 0
                    // Raid-exclusive bosses excluded per user request 2026-07-26 —
                    // the only two raid subfolders that exist in the data.
                    || path.IndexOf("/SurturRaid/", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("/OnslaughtRaid/", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                if (proto.BehaviorProfile == null || proto.BehaviorProfile.Brain == PrototypeId.Invalid) continue;

                entries.Add(new BossCatalogEntry
                {
                    ProtoRef = $"0x{(ulong)agentRef:X16}",
                    Name = ExtractLeaf(path),
                    Path = path,
                    PortraitPath = GameDatabase.GetAssetName(iconAssetId),
                });
            }

            // Curated whitelist (itembase.mhbugle.com villains list minus
            // raids/dummies, 2026-07-26) — narrows the raw /Bosses/ dump
            // (mostly per-event/per-chapter reskins) down to one entry per
            // recognizable named villain. See CuratedBossRoster.cs.
            entries = MHServerEmu.Games.Entities.CuratedBossRoster.SelectCanonical(entries, e => e.Name);

            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new { Ok = true, Count = entries.Count, Bosses = entries };
        }

        private sealed class BossCatalogEntry
        {
            public string ProtoRef { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public string PortraitPath { get; set; }
        }

        private static string ExtractLeaf(string path)
        {
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }
    }

    public class BossRosterSpawnWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        // Tracks bosses spawned via THIS handler, per player db id, so
        // /webapi/bossroster/clear can despawn just those (not every entity
        // in the region). In-memory only, process-lifetime — same
        // convention as the rest of this codebase's debug/test tooling.
        internal static readonly Dictionary<ulong, List<ulong>> SpawnedByPlayer = new();

        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();

            string playerName = null;
            ulong bossRef = 0;
            int count = 1;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("bossRef", out var br)) bossRef = PhantomsWebUtil.ParseRef(br.GetString());
                if (root.TryGetProperty("count", out var cn)) count = cn.GetInt32();
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (bossRef == 0)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing or invalid 'bossRef'" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string findError);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = findError ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                var avatar = p.CurrentAvatar;
                if (avatar == null || avatar.IsInWorld == false)
                    return new { Ok = false, Error = "player has no avatar in world", Spawned = 0, Failed = 0, FirstError = (string)null };

                var bossProto = ((PrototypeId)bossRef).As<AgentPrototype>();
                if (bossProto == null)
                    return new { Ok = false, Error = "bossRef did not resolve to an AgentPrototype", Spawned = 0, Failed = 0, FirstError = (string)null };

                int spawned = 0, failed = 0;
                string firstError = null;
                int clampedCount = Math.Clamp(count, 1, 10);
                var spawnedIds = new List<string>();

                for (int i = 0; i < clampedCount; i++)
                {
                    if (EntityHelper.GetSpawnPositionNearAvatar(avatar, avatar.Region, bossProto.Bounds, 250f, out Vector3 position) == false)
                    {
                        failed++;
                        firstError ??= "no space found to spawn the entity";
                        continue;
                    }

                    Orientation orientation = Orientation.FromDeltaVector(avatar.RegionLocation.Position - position);
                    Agent agent = EntityHelper.CreateAgent(bossProto, avatar, position, orientation);
                    if (agent == null)
                    {
                        failed++;
                        firstError ??= "CreateAgent returned null";
                        continue;
                    }

                    // Same fixups Player.WaveDirector.cs's SpawnCuratedBoss
                    // applies — Dormant clear, AllianceOverride, LootCooldown
                    // fallback, AICustomThinkRateMS, MODOK AI-bootstrap fix.
                    // Without these a manually test-spawned boss can spawn
                    // Dormant, mutually hostile with other enemies, or (MODOK
                    // specifically) stuck in a broken AI state and never
                    // attack/move at all — confirmed live 2026-07-26.
                    EntityHelper.ApplyStandaloneBossFixups(agent, bossProto);

                    spawned++;
                    if (!SpawnedByPlayer.TryGetValue(p.DatabaseUniqueId, out var ids))
                        SpawnedByPlayer[p.DatabaseUniqueId] = ids = new List<ulong>();
                    ids.Add(agent.Id);
                    spawnedIds.Add($"0x{agent.Id:X}");
                }

                return new { Ok = spawned > 0 || failed == 0, Error = (string)null, Spawned = spawned, Failed = failed, FirstError = firstError, EntityIds = spawnedIds };
            });

            Logger.Info($"[BossRoster] spawn for {player.GetName()}: {JsonSerializer.Serialize(result)}");
            await context.SendJsonAsync(result);
        }
    }

    public class BossRosterClearWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();
            PhantomsWebUtil.ParseTarget(body, out string playerName, out string playerDbId);

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                int removed = 0;
                if (BossRosterSpawnWebHandler.SpawnedByPlayer.TryGetValue(p.DatabaseUniqueId, out var ids))
                {
                    foreach (ulong id in ids)
                    {
                        var entity = p.Game.EntityManager.GetEntity<WorldEntity>(id);
                        if (entity == null || entity.IsDestroyed) continue;
                        if (entity.IsInWorld) entity.ExitWorld();
                        entity.Destroy();
                        removed++;
                    }
                    ids.Clear();
                }
                return new { Ok = true, Removed = removed };
            });

            await context.SendJsonAsync(result);
        }
    }
}
