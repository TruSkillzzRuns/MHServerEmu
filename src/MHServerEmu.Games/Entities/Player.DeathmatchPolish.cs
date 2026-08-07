using System;
using Gazillion;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.MetaGames;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Entities
{
    // Deathmatch presentation and balance polish, kept out of the mode's core
    // files. Everything here is driven by values verified against the loaded
    // game data with !dmdata (see DeathmatchDataCommands.cs) rather than assumed.
    public partial class Player
    {
        #region Win/loss rubber band

        // PowerPayload.cs:1404-1410 already scales PvP damage by two curve pairs,
        // indexed by PvPRecentKDRatio and PvPRecentWinLossRatio. Sampled with
        // !dmdata curves:
        //
        //   PvPDamageBoostForKDPct      [0..100] = 0      <- entirely flat
        //   PvPDamageReductionForKDPct  [0..100] = 1.0    <- multiplicative identity
        //   PvPDamageBoostForWinPct     [0]=0.175 -> [70..100]=0
        //   PvPDamageReductionForWinPct [0]=0.875 -> [80..100]=1.0
        //
        // So the KD pair is genuinely inert in the shipped data and is left
        // alone. The win/loss pair is real: a player on a 0% recent win rate
        // deals +17.5% and takes x0.875, tapering to nothing by a ~70-80% win
        // rate. That is the anti-stomp band, and it is what this maintains.
        //
        // The ratio is deliberately NOT written through the shipped
        // PvPDefenderGameMode path — that code is defender-mode only, and its
        // final line reads `Math.Max(1f, recentWins / recentMatches)`, which
        // pins the ratio at 1.0 (index 100 = no adjustment) no matter what the
        // player does. Whether that is a typo for Min is not something this
        // change guesses at; it just keeps its own value on its own property.

        private const int DeathmatchRubberBandWindow = 10;   // matches remembered

        /// <summary>Pushes the current recent-win ratio onto the avatar so the shipped damage curves pick it up.</summary>
        private void ApplyDeathmatchWinLossRubberBand()
        {
            try
            {
                Avatars.Avatar avatar = CurrentAvatar;
                if (avatar == null) return;

                int matches = Math.Clamp((int)Properties[PropertyEnum.PvPMatchCount], 0, DeathmatchRubberBandWindow);
                if (matches <= 0)
                {
                    // No history: sit at the neutral end of the curve rather than
                    // handing a brand-new player the full underdog bonus.
                    avatar.Properties[PropertyEnum.PvPRecentWinLossRatio] = 1.0f;
                    return;
                }

                int wins = Math.Clamp((int)Properties[PropertyEnum.PvPWins], 0, matches);
                float ratio = Math.Clamp((float)wins / matches, 0f, 1f);
                avatar.Properties[PropertyEnum.PvPRecentWinLossRatio] = ratio;

                DeathmatchLogger.Info($"[TDM] rubber band: {wins}/{matches} recent wins -> PvPRecentWinLossRatio {ratio:0.###} " +
                                      $"(+{GetDeathmatchBoostPct(ratio):0.#}% damage dealt)");
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] rubber band failed: {ex.Message}"); }
        }

        /// <summary>Reads back what the shipped curve will actually apply, for the log line above.</summary>
        private float GetDeathmatchBoostPct(float ratio)
        {
            if (_deathmatchMetaGameId == 0) return 0f;
            if (Game.EntityManager.GetEntity<PvP>(_deathmatchMetaGameId) is not PvP pvp) return 0f;
            return (pvp.PvPPrototype?.GetDamageBoostForWinPct(ratio) ?? 0f) * 100f;
        }

        /// <summary>Records the match result so the next match's rubber band reflects it.</summary>
        private void RecordDeathmatchResult(bool won)
        {
            try
            {
                Properties.AdjustProperty(1, won ? PropertyEnum.PvPWins : PropertyEnum.PvPLosses);
                DeathmatchLogger.Info($"[TDM] result recorded: {(won ? "WIN" : "LOSS")} " +
                                      $"(lifetime {(int)Properties[PropertyEnum.PvPWins]}W / {(int)Properties[PropertyEnum.PvPLosses]}L)");
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] result record failed: {ex.Message}"); }
        }

        #endregion

        #region Finale lootsplosion

        // Every ordinary kill drops NOTHING now: deathmatch phantoms are spawned
        // with PropertyEnum.NoLootDrop (blocks WorldEntity.AwardKillLoot's death
        // loot at WorldEntity.cs:3973 and AwardHitLoot's mid-fight drops at
        // :4033) and PropertyEnum.NoExpOnDeath (blocks the XP/orb award at
        // :4011 — a SEPARATE gate, which is why orbs kept falling when only
        // NoLootDrop was set). Credits and Eternity Splinters still pay per
        // kill, but those go straight into the currency properties rather than
        // onto the ground, so they are unaffected.
        //
        // All of the loot for a match lands here instead, on the final kill.

        /// <summary>
        /// Loot/Tables/Mob/Bosses/EndgameDailies/Subtables/SharedEndgameDailiesCosmicBUFFED.
        /// The same confirmed-real endgame table Trial of the Impossible's
        /// finale chest and the Bounty Board baseline already use — reused
        /// rather than inventing an unverified ref.
        /// </summary>
        private const ulong DeathmatchFinaleLootTableRef = 0x0520D1A142CA23CD;

        /// <summary>Number of separate rolls in the finale burst.</summary>
        private const int DeathmatchFinaleLootRolls = 12;

        // Rarity refs resolved BY PATH, not by GlobalsPrototype.RarityCosmic /
        // RarityUnique and not via GetEndlessChestAllowedRarities.
        //
        // Why: the rarity ladder has NINE prototypes with collisions on tiers 5
        // and 6 —
        //   [T5] R5Cosmic, R5Ultimate
        //   [T6] R6Omega,  R6Runewords, R6Unique
        // but Avatar.EnsureRarityTiers builds its tier map with
        // map.TryAdd(proto.Tier, ref), so the FIRST prototype per tier wins and
        // R5Cosmic / R6Omega / R6Unique are silently dropped. Anything that
        // resolves a band through that map (GetEndlessChestAllowedRarities, the
        // phantom gear bands) therefore gets R5Ultimate / R6Runewords instead of
        // the rarities actually wanted. Naming them directly avoids the whole
        // problem and is version-safe, since paths are stable across 1.48/1.52/1.53
        // while ids are not.
        private const string RarityCosmicPath = "Entity/Items/Rarity/R5Cosmic.prototype";
        private const string RarityUniquePath = "Entity/Items/Rarity/R6Unique.prototype";
        private const string RarityOmegaPath = "Entity/Items/Rarity/R6Omega.prototype";

        /// <summary>Level at which the finale switches to the fixed top-tier band.</summary>
        private const int DeathmatchEndgameLevel = 60;

        /// <summary>
        /// Radius of the local entity query used to find this burst's own items.
        /// Loot lands on the spawn grid around the drop point, so this only has
        /// to cover that grid — scanning the whole region is what caused the
        /// frame hitch.
        /// </summary>
        private const float DeathmatchLootScanRadius = 2000f;

        /// <summary>
        /// Path prefix of the terminal endgame-boss loot tables. Every terminal
        /// boss's artifact drops live under here, one table per boss — verified
        /// with !dmdata bossloot Terminals, which showed each terminal boss
        /// carrying an OnKilledMiniBoss table under
        /// Loot/Tables/Mob/Bosses/EndgameDailies/Terminals/&lt;Terminal&gt;/&lt;Boss&gt;...
        /// </summary>
        private const string DeathmatchTerminalLootPathPrefix = "Loot/Tables/Mob/Bosses/EndgameDailies/Terminals/";

        /// <summary>
        /// Terminal boss artifact tables, resolved once by path.
        ///
        /// Deliberately NOT the single generic Loot/Tables/Mob/Bosses/CosmicArtifactsTable
        /// this used to roll. That table holds 48 cosmic artifacts, but a set
        /// comparison against the terminal tables (walked with !dmdata tabletree)
        /// found Art361Cosmic / Art367Cosmic / Art368Cosmic present on terminal
        /// bosses and ABSENT from it — so it is not a superset, and rolling it
        /// would silently exclude drops that terminal bosses really do give.
        ///
        /// Resolved by path rather than hardcoded ids so v48/v52/v53 each pick up
        /// their own data without three separate id lists.
        /// </summary>
        private static List<PrototypeId> s_deathmatchTerminalArtifactTables;

        private static List<PrototypeId> GetDeathmatchTerminalArtifactTables()
        {
            if (s_deathmatchTerminalArtifactTables != null)
                return s_deathmatchTerminalArtifactTables;

            List<PrototypeId> tables = new();

            foreach (PrototypeId protoRef in GameDatabase.DataDirectory
                .IteratePrototypesInHierarchy<GameData.Prototypes.LootTablePrototype>(GameData.PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                string name = GameDatabase.GetPrototypeName(protoRef);
                if (name == null) continue;
                if (name.StartsWith(DeathmatchTerminalLootPathPrefix, StringComparison.Ordinal) == false) continue;

                // Only the artifact tables. A terminal boss also carries gear /
                // shared tables under the same folder, and those would reintroduce
                // exactly the sub-Cosmic junk the gear band exists to keep out.
                if (name.Contains("Artifact", StringComparison.OrdinalIgnoreCase) == false) continue;

                tables.Add(protoRef);
            }

            DeathmatchLogger.Info($"[TDM] resolved {tables.Count} terminal boss artifact table(s) under {DeathmatchTerminalLootPathPrefix}");

            s_deathmatchTerminalArtifactTables = tables;
            return tables;
        }

        /// <summary>
        /// Every artifact prototype the terminal boss tables can produce.
        ///
        /// Needed because the GEAR table drops artifacts too. Measured live
        /// 2026-08-05: one finale put 402 items on the ground from 14 rolls, and
        /// with the sweep exempting all artifacts unconditionally, every low-level
        /// artifact the gear table rolled survived — which is exactly the "same low
        /// level type artifacts" still showing up. Exempting artifacts by TYPE was
        /// too broad; the exemption has to be by IDENTITY.
        /// </summary>
        private static HashSet<PrototypeId> s_deathmatchTerminalArtifactItems;

        private static HashSet<PrototypeId> GetDeathmatchTerminalArtifactItems()
        {
            if (s_deathmatchTerminalArtifactItems != null)
                return s_deathmatchTerminalArtifactItems;

            HashSet<PrototypeId> items = new();
            HashSet<PrototypeId> visited = new();

            foreach (PrototypeId tableRef in GetDeathmatchTerminalArtifactTables())
                CollectArtifactItems(tableRef.As<GameData.Prototypes.LootTablePrototype>(), items, visited, 0);

            DeathmatchLogger.Info($"[TDM] terminal boss artifact pool: {items.Count} distinct artifact(s)");

            s_deathmatchTerminalArtifactItems = items;
            return items;
        }

        private static void CollectArtifactItems(GameData.Prototypes.LootTablePrototype table,
            HashSet<PrototypeId> items, HashSet<PrototypeId> visited, int depth)
        {
            if (table == null || depth > 8) return;
            if (visited.Add(table.DataRef) == false) return;
            if (table.Choices == null) return;

            foreach (GameData.Prototypes.LootNodePrototype node in table.Choices)
            {
                switch (node)
                {
                    case GameData.Prototypes.LootTablePrototype nested:
                        CollectArtifactItems(nested, items, visited, depth + 1);
                        break;

                    case GameData.Prototypes.LootDropItemPrototype drop when drop.Item is ArtifactPrototype:
                        items.Add(drop.Item.DataRef);
                        break;
                }
            }
        }

        /// <summary>Artifact rolls in the finale burst, endgame only.</summary>
        private const int DeathmatchFinaleArtifactRolls = 2;

        /// <summary>
        /// Multiplier applied to the terminal artifact tables' 1% drop chance.
        /// 100x turns 0.01 into a certainty, so a won match reliably pays out its
        /// two artifacts instead of relying on a 1-in-100 roll.
        /// </summary>
        private const float DeathmatchArtifactDropBoost = 100f;

        /// <summary>Gap between individual finale rolls, so no single frame does them all.</summary>
        private const int DeathmatchFinaleRollIntervalMs = 100;

        private readonly EventPointer<DeathmatchFinaleLootEvent> _tdmFinaleLoot = new();
        private Core.VectorMath.Vector3 _tdmFinaleDropPos;
        private HashSet<ulong> _tdmFinalePreExisting;
        private List<PrototypeId> _tdmFinaleBand;
        private bool _tdmFinaleEndgame;
        private ulong _tdmFinaleRegionId;
        private int _tdmFinaleGearRollsLeft;
        private int _tdmFinaleArtifactRollsLeft;
        private long _tdmFinaleRollMs;

        private void ScheduleDeathmatchFinaleRoll()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_tdmFinaleLoot.IsValid) scheduler.CancelEvent(_tdmFinaleLoot);
            scheduler.ScheduleEvent(_tdmFinaleLoot, TimeSpan.FromMilliseconds(DeathmatchFinaleRollIntervalMs), _deathmatchFinaleEvents);
            _tdmFinaleLoot.Get()?.Initialize(this);
        }

        /// <summary>
        /// Rolls ONE item per tick — gear first, then the boss artifact table —
        /// and sweeps once the last roll lands.
        /// </summary>
        private void DoDeathmatchFinaleRoll()
        {
            try
            {
                Regions.Region region = Game.RegionManager.GetRegion(_tdmFinaleRegionId);
                if (region == null) { _tdmFinaleGearRollsLeft = 0; _tdmFinaleArtifactRollsLeft = 0; return; }

                Avatars.Avatar avatar = CurrentAvatar;
                if (avatar == null) { _tdmFinaleGearRollsLeft = 0; _tdmFinaleArtifactRollsLeft = 0; return; }

                bool gear = _tdmFinaleGearRollsLeft > 0;

                PrototypeId tableRef;
                if (gear)
                {
                    tableRef = (PrototypeId)DeathmatchFinaleLootTableRef;
                }
                else
                {
                    // A different terminal boss each artifact roll, so the pool is
                    // the whole terminal-boss artifact set rather than one boss's
                    // two or three personal drops.
                    List<PrototypeId> terminalTables = GetDeathmatchTerminalArtifactTables();
                    if (terminalTables.Count == 0)
                    {
                        DeathmatchLogger.Warn("[TDM] no terminal boss artifact tables resolved — skipping artifact roll");
                        _tdmFinaleArtifactRollsLeft = 0;
                        return;
                    }

                    tableRef = terminalTables[Game.Random.Next(terminalTables.Count)];
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                using Loot.LootInputSettings inputSettings = Core.Memory.ObjectPoolManager.Instance.Get<Loot.LootInputSettings>();
                inputSettings.Initialize(Loot.LootContext.Drop, this, avatar, _tdmFinaleDropPos);
                inputSettings.LootRollSettings.Level = avatar.CharacterLevel;
                inputSettings.LootRollSettings.LevelForRequirementCheck = avatar.CharacterLevel;

                // The artifact table rolls on its own ladder — forcing the gear
                // band onto it would filter every artifact out.
                if (gear && _tdmFinaleBand != null && _tdmFinaleBand.Count > 0)
                {
                    inputSettings.LootRollSettings.Rarities.Clear();
                    foreach (PrototypeId r in _tdmFinaleBand)
                        inputSettings.LootRollSettings.Rarities.Add(r);
                }

                // Terminal artifact tables carry NoDropPercent=0.99 — a real
                // terminal boss gives a cosmic artifact about 1% of the time
                // (verified with !dmdata tabletree on DoctorDoomCosmicArtifacts).
                // Two rolls at 1% is 0.02 expected artifacts, which is why a won
                // match produced none at all.
                //
                // ItemResolverContext.GetDropChance computes 1 - NoDropPercent and
                // then multiplies by NoDropModifier, but only when the
                // DifficultyTierNoDropModified flag is set — so setting both turns
                // the 1% into a certainty without editing the shipped table or
                // changing which artifacts are eligible. The flag is not one of the
                // hard restrictions in IsRestrictedByLootDropChanceModifier.
                if (gear == false)
                {
                    inputSettings.LootRollSettings.DropChanceModifiers |= Loot.LootDropChanceModifiers.DifficultyTierNoDropModified;
                    inputSettings.LootRollSettings.NoDropModifier = DeathmatchArtifactDropBoost;
                }

                Game.LootManager.SpawnLootFromTable(tableRef, inputSettings, 1, out int numDrops);

                if (gear == false)
                    DeathmatchLogger.Info($"[TDM] artifact roll from {tableRef.GetNameFormatted()} produced {numDrops} drop(s)");

                sw.Stop();
                _tdmFinaleRollMs += sw.ElapsedMilliseconds;

                if (gear) _tdmFinaleGearRollsLeft--;
                else _tdmFinaleArtifactRollsLeft--;

                if (_tdmFinaleGearRollsLeft > 0 || _tdmFinaleArtifactRollsLeft > 0)
                {
                    ScheduleDeathmatchFinaleRoll();
                    return;
                }

                // Last roll landed — cull anything outside the band now that
                // everything is on the ground.
                var sweepSw = System.Diagnostics.Stopwatch.StartNew();
                int culled = SweepDeathmatchFinaleLoot(region, _tdmFinaleDropPos, _tdmFinalePreExisting, _tdmFinaleBand);
                sweepSw.Stop();

                DeathmatchLogger.Info($"[TDM] finale lootsplosion done: culled {culled} " +
                                      $"| PERF rolls={_tdmFinaleRollMs}ms spread over ticks, sweep={sweepSw.ElapsedMilliseconds}ms");

                _tdmFinalePreExisting = null;
                _tdmFinaleBand = null;
            }
            catch (Exception ex)
            {
                DeathmatchLogger.Warn($"[TDM] finale roll failed: {ex.Message}");
                _tdmFinaleGearRollsLeft = 0;
                _tdmFinaleArtifactRollsLeft = 0;
            }
        }

        private sealed class DeathmatchFinaleLootEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.DoDeathmatchFinaleRoll();
        }

        /// <summary>Drops the whole match's loot in one burst where the final victim fell.</summary>
        private void SpawnDeathmatchFinaleLoot(ulong finalVictimId)
        {
            try
            {
                Avatars.Avatar avatar = CurrentAvatar;
                if (avatar == null || avatar.IsInWorld == false) return;

                if (avatar.Region == null) return;

                // Drop where the last combatant fell if it is still in world,
                // otherwise at the player's feet.
                Core.VectorMath.Vector3 dropPos = avatar.RegionLocation.Position;
                if (finalVictimId != 0
                    && Game.EntityManager.GetEntity<WorldEntity>(finalVictimId) is WorldEntity victim
                    && victim.IsInWorld)
                {
                    dropPos = victim.RegionLocation.Position;
                }

                int playerLevel = avatar.CharacterLevel;
                bool endgame = playerLevel >= DeathmatchEndgameLevel;
                List<PrototypeId> rarities = GetDeathmatchFinaleRarities(endgame);

                // Snapshot the ground items that already existed so the sweep
                // below only ever touches what THIS burst created.
                //
                // Scoped to a sphere around the drop point, NOT region.Entities.
                // Iterating every entity in the region is one of the two causes
                // this fork already identified for loot stutter — see the
                // instrumentation note on Player.WaveDirector.DespawnLeftoverGroundLoot,
                // "this iterates EVERY entity in the region (region.Entities) ...
                // a real candidate for the still-reported stutter". This code was
                // doing that twice per lootsplosion.
                var perfSw = System.Diagnostics.Stopwatch.StartNew();

                HashSet<ulong> preExisting = new();
                Regions.Region lootRegion = avatar.Region;
                var scanSphere = new Core.Collisions.Sphere(dropPos, DeathmatchLootScanRadius);
                // AllPartitions, NOT PrimaryPartition. Dropped loot is instanced
                // per-player and therefore lives in a player-restricted partition
                // (EntityRegionSpatialPartition.cs:19 "Player-specific entities
                // (e.g. instanced loot)"). IterateElementsInVolume only pushes
                // _primaryPartition for that flag, so a PrimaryPartition query
                // returns ZERO ground items — which is why the cull below reported
                // "culled 0" while greens and blues sat on the ground.
                var scanCtx = new EntityRegionSPContext(EntityRegionSPContextFlags.AllPartitions);
                foreach (WorldEntity e in lootRegion.IterateEntitiesInVolume(scanSphere, scanCtx))
                    if (e is Items.Item preItem) preExisting.Add(preItem.Id);

                long snapshotMs = perfSw.ElapsedMilliseconds;

                using Loot.LootInputSettings inputSettings = Core.Memory.ObjectPoolManager.Instance.Get<Loot.LootInputSettings>();
                inputSettings.Initialize(Loot.LootContext.Drop, this, avatar, dropPos);

                // Roll everything at the player's own level. Below 60 this is the
                // whole story: no forced band, so a low-level match pays out
                // level-appropriate gear instead of endgame items the player
                // cannot use.
                inputSettings.LootRollSettings.Level = playerLevel;
                inputSettings.LootRollSettings.LevelForRequirementCheck = playerLevel;

                if (rarities.Count > 0)
                {
                    inputSettings.LootRollSettings.Rarities.Clear();
                    foreach (PrototypeId r in rarities)
                        inputSettings.LootRollSettings.Rarities.Add(r);
                }

                perfSw.Stop();

                // Hand the rolls to a scheduled drip instead of running them all
                // here. Measured live 2026-08-05: snapshot=0ms sweep=1ms but
                // rolls=350ms for 12 rolls (~29ms each) — a 350ms block of the
                // game thread, which is the freeze. One roll per scheduled tick
                // turns that into ~29ms of work per frame.
                _tdmFinaleDropPos = dropPos;
                _tdmFinalePreExisting = preExisting;
                _tdmFinaleBand = rarities;
                _tdmFinaleEndgame = endgame;
                _tdmFinaleRegionId = lootRegion.Id;
                _tdmFinaleGearRollsLeft = DeathmatchFinaleLootRolls;
                _tdmFinaleArtifactRollsLeft = endgame ? DeathmatchFinaleArtifactRolls : 0;
                _tdmFinaleRollMs = 0;

                DeathmatchLogger.Info($"[TDM] finale lootsplosion queued at {dropPos.ToStringNames()}: " +
                                      $"{DeathmatchFinaleLootRolls} gear + {_tdmFinaleArtifactRollsLeft} artifact roll(s), " +
                                      $"level {playerLevel}, band {(rarities.Count == 0 ? "<level-appropriate>" : string.Join(",", rarities.Select(r => r.GetNameFormatted())))} " +
                                      $"| PERF snapshot={snapshotMs}ms");

                ScheduleDeathmatchFinaleRoll();

                SendBannerLines(endgame
                    ? "Match loot dropped — Cosmic and above only."
                    : "Match loot dropped.");
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] finale lootsplosion failed: {ex.Message}"); }
        }

        /// <summary>
        /// The rarity band for the finale. Empty below level 60, which leaves the
        /// normal level-based roll alone so a low-level match pays out gear the
        /// player can actually equip.
        ///
        /// At 60 it is Cosmic plus the top set rarity — Unique on 1.48/1.52,
        /// replaced by Omega on 1.53.
        /// </summary>
        private static List<PrototypeId> GetDeathmatchFinaleRarities(bool endgame)
        {
            List<PrototypeId> band = new(2);
            if (endgame == false) return band;

            PrototypeId cosmic = GameDatabase.GetPrototypeRefByName(RarityCosmicPath);
            if (cosmic != PrototypeId.Invalid) band.Add(cosmic);
            else DeathmatchLogger.Warn($"[TDM] {RarityCosmicPath} does not resolve on this game version");

#if GAME_VERSION_1_53
            const string topPath = RarityOmegaPath;
#else
            const string topPath = RarityUniquePath;
#endif
            PrototypeId top = GameDatabase.GetPrototypeRefByName(topPath);
            if (top != PrototypeId.Invalid) band.Add(top);
            else DeathmatchLogger.Warn($"[TDM] {topPath} does not resolve on this game version");

            return band;
        }

        /// <summary>
        /// Destroys gear the burst produced that falls outside the allowed rarity
        /// band. Artifacts are exempt — their pool is controlled at the source by
        /// the boss table they roll from. Returns how many items were removed.
        ///
        /// This exists because setting LootRollSettings.Rarities is a REQUEST,
        /// not a guarantee — ItemResolver.ResolveRarity drops the filter entirely
        /// when no item in the table can satisfy it
        /// (ItemResolver.cs:344-353 skips rarities failing IsDroppableForRestrictions,
        /// and an empty entry list simply returns Invalid). Checking what actually
        /// hit the ground is the only way to be certain.
        /// </summary>
        private int SweepDeathmatchFinaleLoot(Regions.Region region, Core.VectorMath.Vector3 dropPos,
            HashSet<ulong> preExisting, List<PrototypeId> allowedRarities)
        {
            if (region == null) return 0;

            List<Items.Item> doomed = new();

            // Same local sphere as the snapshot — loot lands on the spawn grid
            // around dropPos, so a region-wide scan buys nothing and costs frame
            // time on a busy map.
            var sweepSphere = new Core.Collisions.Sphere(dropPos, DeathmatchLootScanRadius);
            // Must match the snapshot's partition set — see the note there.
            var sweepCtx = new EntityRegionSPContext(EntityRegionSPContextFlags.AllPartitions);

            int seen = 0;
            foreach (WorldEntity e in region.IterateEntitiesInVolume(sweepSphere, sweepCtx))
            {
                if (e is not Items.Item item) continue;
                if (preExisting.Contains(item.Id)) continue;      // not from this burst

                seen++;

                Items.ItemSpec spec = item.ItemSpec;
                if (spec == null) continue;

                // Artifacts skip the gear rarity band — they roll on their own
                // ladder — but they are NOT exempt from the sweep. Only artifacts a
                // terminal boss can actually drop are kept; anything else is the
                // gear table's own artifact output and gets culled. No level floor
                // is involved: identity is the filter, exactly as requested.
                if (item.Prototype is ArtifactPrototype)
                {
                    if (GetDeathmatchTerminalArtifactItems().Contains(item.PrototypeDataRef) == false)
                    {
                        doomed.Add(item);
                        DeathmatchLogger.Info($"[TDM] cull artifact {item.PrototypeName} — not a terminal boss drop");
                    }
                    continue;
                }

                if (allowedRarities.Count > 0 && allowedRarities.Contains(spec.RarityProtoRef) == false)
                {
                    doomed.Add(item);
                    DeathmatchLogger.Info($"[TDM] cull {item.PrototypeName} rarity {spec.RarityProtoRef.GetNameFormatted()} outside band");
                }
            }

            DeathmatchLogger.Info($"[TDM] sweep saw {seen} new ground item(s), culling {doomed.Count}");

            foreach (Items.Item item in doomed)
            {
                try { item.Destroy(); }
                catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] could not cull {item.Id}: {ex.Message}"); }
            }

            return doomed.Count;
        }

        #endregion

        #region Match-start countdown

        // Three banners a second apart, then the shipped PvP welcome sting.
        //
        // Deliberately NOT using MetaGameModePrototype.UITimedBannersOnActivate:
        // the only shipped countdown strings are DefenderPvP's, and they read
        // "Waiting for match to start! $intargone$ seconds remain" / "Match
        // started! Destroy the enemy guardians!" — the second is plainly wrong
        // for this mode. Custom strings out of AchievementStringMap_95_Deathmatch
        // are used instead, sent as ordinary banner messages, which is a path
        // already proven by the VICTORY / DEFEAT banner.

        private const ulong DmStrGo = 9910000000000020;
        private const ulong DmStrCountdown3 = 9910000000000021;
        private const ulong DmStrCountdown2 = 9910000000000022;
        private const ulong DmStrCountdown1 = 9910000000000023;

        /// <summary>
        /// PvP_MatchStart, from Audio/Types/MetaGameTheme.type (enumerated with
        /// !dmdata audio — that type has 25 members and is the only pool
        /// SendPlayUISoundTheme is used with anywhere in the shipped code).
        ///
        /// Replaces PvP_Welcome (2980640826322588369), which is what
        /// DefenderPvP's *Staging* mode plays — the lobby/waiting phase, not the
        /// start of a match. PvP_MatchStart is referenced by no shipped
        /// prototype at all, so nothing else in the game competes for it.
        ///
        /// NOTE: the name is the only evidence for what this actually sounds
        /// like. It has never been played on this server before now.
        /// </summary>
        private const ulong DmAudioMatchStart = 11169590270811050763;

        private const int DeathmatchCountdownStepMs = 1000;

        #region Match time limit

        /// <summary>
        /// Hard cap on match length. Without one a match can only end by someone
        /// reaching the kill target, and nothing guarantees that happens — a stalled
        /// fight, a phantom stuck out of reach, or simply an even three-way can run
        /// forever. On expiry the highest score wins.
        /// </summary>
        private const int DeathmatchTimeLimitMs = 12 * 60 * 1000;

        /// <summary>Warning banner this long before the cap.</summary>
        private const int DeathmatchTimeWarningMs = 60 * 1000;

        private readonly EventPointer<DeathmatchTimeWarningEvent> _tdmTimeWarning = new();
        private readonly EventPointer<DeathmatchTimeUpEvent> _tdmTimeUp = new();

        private void StartDeathmatchMatchTimer()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;

            if (_tdmTimeWarning.IsValid) scheduler.CancelEvent(_tdmTimeWarning);
            if (_tdmTimeUp.IsValid) scheduler.CancelEvent(_tdmTimeUp);

            scheduler.ScheduleEvent(_tdmTimeWarning, TimeSpan.FromMilliseconds(DeathmatchTimeLimitMs - DeathmatchTimeWarningMs), _deathmatchEvents);
            _tdmTimeWarning.Get()?.Initialize(this);

            scheduler.ScheduleEvent(_tdmTimeUp, TimeSpan.FromMilliseconds(DeathmatchTimeLimitMs), _deathmatchEvents);
            _tdmTimeUp.Get()?.Initialize(this);

            DeathmatchLogger.Info($"[TDM] match timer armed — {DeathmatchTimeLimitMs / 60000} minute cap, warning at {DeathmatchTimeWarningMs / 1000}s remaining");
        }

        private void DoDeathmatchTimeWarning()
        {
            if (_tdmActive == false) return;
            SendBannerLines($"One minute remaining — highest score wins.");
            DeathmatchLogger.Info("[TDM] match timer: one minute warning");
        }

        /// <summary>Time cap reached — highest score takes it. A tie for the lead is a draw.</summary>
        private void DoDeathmatchTimeUp()
        {
            if (_tdmActive == false) return;

            int best = -1;
            for (int i = 0; i < _tdmTeamCount; i++)
                if (_tdmTeamScores[i] > best) best = _tdmTeamScores[i];

            int leaders = 0, leadTeam = -1;
            for (int i = 0; i < _tdmTeamCount; i++)
                if (_tdmTeamScores[i] == best) { leaders++; leadTeam = i; }

            bool drawn = leaders > 1;
            bool won = drawn == false && leadTeam == _tdmMyTeam;

            string verdict = drawn
                ? $"TIME — DRAW at {best}"
                : won
                    ? $"TIME — VICTORY, {best} to {Others(leadTeam)}"
                    : $"TIME — DEFEAT, {DeathmatchTeamName(leadTeam)} leads with {best}";

            DeathmatchLogger.Info($"[TDM] time limit reached — scores {string.Join(" / ", _tdmTeamScores)}, {verdict}");

            try
            {
                SendBannerMessage(
                    (LocaleStringId)(won ? DmStrVictory : DmStrDefeat),
                    won ? TextStylePrototype.BannerMessageAlert : TextStylePrototype.BannerMessageError,
                    timeToLiveMS: DeathmatchVerdictBannerMS,
                    messageStyle: BannerMessageStyle.FlyIn,
                    doNotQueue: true,
                    showImmediately: true);
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] time-up banner failed: {ex.Message}"); }

            SendBannerLines(verdict);

            // A draw is not a win: no rubber-band record and no loot, matching the
            // rule that only a winner gets the finale burst.
            if (drawn == false)
            {
                RecordDeathmatchResult(won);
                if (won) SpawnDeathmatchFinaleLoot(0);
            }

            EndDeathmatch(verdict, returnHome: true);
        }

        private sealed class DeathmatchTimeWarningEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.DoDeathmatchTimeWarning();
        }

        private sealed class DeathmatchTimeUpEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.DoDeathmatchTimeUp();
        }

        #endregion

        private readonly EventPointer<DeathmatchCountdownEvent> _tdmCountdown = new();
        private int _tdmCountdownStep;

        private void StartDeathmatchCountdown()
        {
            // The match-start sting is sent through the mode so it reaches the
            // same client set every other metagame sound does.
            try
            {
                if (_deathmatchMetaGameId != 0
                    && Game.EntityManager.GetEntity<PvP>(_deathmatchMetaGameId) is PvP pvp
                    && pvp.CurrentMode != null)
                {
                    pvp.CurrentMode.SendPlayUISoundTheme((AssetId)DmAudioMatchStart, this);
                    DeathmatchLogger.Info($"[TDM] match-start sting sent (PvP_MatchStart 0x{DmAudioMatchStart:X})");
                }
                else
                {
                    DeathmatchLogger.Warn("[TDM] match-start sting NOT sent — no active MetaGame mode");
                }
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] match-start sting failed: {ex.Message}"); }

            LockDeathmatchCombatantsForCountdown(true);

            _tdmCountdownStep = 3;
            ScheduleDeathmatchCountdown();
        }

        /// <summary>
        /// Holds every combatant still until "FIGHT!". Without this the countdown
        /// is decorative — the player can walk off and start swinging on "3".
        ///
        /// SystemImmobilized + Invulnerable + PowerLock is the shipped PvP lock,
        /// copied from PvPDefenderGameMode.AssignPowerLock / UnassignPowerLockForPlayer
        /// (PvPDefenderGameMode.cs:549-570) — the same three properties that mode
        /// uses to freeze players between rounds, applied and removed together.
        ///
        /// Phantoms get it too, so nobody gets a free opening move. Their
        /// invulnerability goes through the TIMED registry rather than a bare
        /// property write: the stuck-invulnerability watchdog force-clears
        /// Invulnerable on a phantom within about a second otherwise, which would
        /// unfreeze them mid-countdown. The expiry is a backstop only — the
        /// unlock below is what normally lifts it.
        /// </summary>
        private void LockDeathmatchCombatantsForCountdown(bool locked)
        {
            Avatars.Avatar avatar = CurrentAvatar;
            if (avatar != null)
            {
                if (locked)
                {
                    avatar.Properties[PropertyEnum.SystemImmobilized] = true;
                    avatar.Properties[PropertyEnum.Invulnerable] = true;
                    avatar.Properties[PropertyEnum.PowerLock] = true;
                }
                else
                {
                    avatar.Properties.RemoveProperty(PropertyEnum.SystemImmobilized);
                    avatar.Properties.RemoveProperty(PropertyEnum.Invulnerable);
                    avatar.Properties.RemoveProperty(PropertyEnum.PowerLock);
                }
            }

            // Countdown is 3 ticks plus the GO tick; give the backstop headroom.
            long untilMs = (Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond) + (DeathmatchCountdownStepMs * 5);

            foreach (ulong id in _tdmCombatantTeam.Keys)
            {
                if (id == avatar?.Id) continue;
                if (Game.EntityManager.GetEntity<Agent>(id) is not Agent phantom) continue;

                try
                {
                    if (locked)
                    {
                        phantom.Locomotor?.Stop();
                        phantom.Properties[PropertyEnum.SystemImmobilized] = true;
                        phantom.Properties[PropertyEnum.Invulnerable] = true;
                        phantom.Properties[PropertyEnum.PowerLock] = true;
                        Avatars.Avatar.MarkPhantomTimedInvincible(id, untilMs);
                    }
                    else
                    {
                        phantom.Properties.RemoveProperty(PropertyEnum.SystemImmobilized);
                        phantom.Properties[PropertyEnum.Invulnerable] = false;
                        phantom.Properties[PropertyEnum.PowerLock] = false;
                        Avatars.Avatar.ClearPhantomTimedInvincible(id);
                    }
                }
                catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] countdown lock failed on {id}: {ex.Message}"); }
            }

            DeathmatchLogger.Info($"[TDM] countdown lock {(locked ? "APPLIED" : "lifted")} on {_tdmCombatantTeam.Count} combatant(s)");
        }

        private void ScheduleDeathmatchCountdown()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_tdmCountdown.IsValid) scheduler.CancelEvent(_tdmCountdown);
            scheduler.ScheduleEvent(_tdmCountdown, TimeSpan.FromMilliseconds(DeathmatchCountdownStepMs), _deathmatchEvents);
            _tdmCountdown.Get()?.Initialize(this);
        }

        private void DoDeathmatchCountdownTick()
        {
            if (_tdmActive == false) return;

            ulong text = _tdmCountdownStep switch
            {
                3 => DmStrCountdown3,
                2 => DmStrCountdown2,
                1 => DmStrCountdown1,
                _ => DmStrGo,
            };

            bool isGo = _tdmCountdownStep <= 0;

            SendBannerMessage(
                (LocaleStringId)text,
                isGo ? TextStylePrototype.BannerMessageAlert : TextStylePrototype.BannerMessageStandard,
                timeToLiveMS: isGo ? 3000 : DeathmatchCountdownStepMs,
                messageStyle: isGo ? BannerMessageStyle.FlyIn : BannerMessageStyle.Standard,
                doNotQueue: true,
                showImmediately: true);

            if (isGo)
            {
                LockDeathmatchCombatantsForCountdown(false);

                // Clock starts on FIGHT!, not on match setup — the countdown and
                // the region load should not eat into the match time.
                StartDeathmatchMatchTimer();
                return;
            }

            _tdmCountdownStep--;
            ScheduleDeathmatchCountdown();
        }

        private sealed class DeathmatchCountdownEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.DoDeathmatchCountdownTick();
        }

        #endregion
    }
}
