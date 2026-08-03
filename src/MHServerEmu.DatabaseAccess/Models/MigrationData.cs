using Gazillion;

namespace MHServerEmu.DatabaseAccess.Models
{
    public class MigrationData
    {
        // Store everything here as ulong, PropertyCollection will sort it out game-side
        private readonly Dictionary<ulong, List<(ulong, ulong)>> _properties = new(32);

        public bool IsInErrorState { get; set; }
        public bool SkipNextUpdate { get; set; }

        public bool IsFirstLoad { get; set; } = true;

        public List<(ulong, ulong)> WorldView { get; } = new();
        public byte[] MatchQueueStatus { get; set; }
        public List<CommunityMemberBroadcast> CommunityStatus { get; } = new();

        // Phantom-hero cross-region persistence. Cross-region transfer destroys
        // the old Game instance (Player + Avatar + phantom entities all go
        // away). MigrationData is the only object that rides with the human
        // across the transfer. We snapshot phantoms as (avatarRef, level,
        // username) here at BeginRegionTransfer; the arriving Avatar reads
        // this on OnEnteredWorld and re-spawns them fresh in the new region.
        public List<PhantomIntent> PhantomIntents { get; } = new();

        /// <summary>
        /// Whether the Rogue Encounter opt-in was on before the last region
        /// transfer. Copied out by SnapshotPhantomsForTransfer and read back
        /// after the new Player entity finishes loading in the next region
        /// so the setting doesn't reset every time the player warps.
        /// </summary>
        public bool RogueEncounterEnabled { get; set; }

        /// <summary>
        /// Nemesis roster — enemy phantom heroes that have killed this player.
        /// Each entry tracks the hero and how many times they've closed the
        /// kill loop; higher rank = tougher when they show up again.
        /// Cross-region persistent alongside RogueEncounterEnabled.
        /// </summary>
        public List<NemesisEntry> Nemeses { get; } = new();

        /// <summary>
        /// Per-hero preferred power priority. Key = avatar PrototypeId (as
        /// ulong), value = the PowerPrototypeId the phantom AI should reach
        /// for first when it's off cooldown and in range. 0 = no preference.
        /// Persists across region transfers.
        /// </summary>
        public Dictionary<ulong, ulong> PreferredPowers { get; } = new();

        /// <summary>
        /// Per-hero phantom combat range preference. Key = AvatarPrototypeId
        /// (as ulong), value = PhantomCombatRangePref (0 = Auto, 1 = Melee,
        /// 2 = Ranged). Absent/0 means Auto, i.e. derive the stop distance from
        /// the kit as before. Persists across region transfers.
        /// </summary>
        public Dictionary<ulong, int> CombatRangePrefs { get; } = new();

        /// <summary>
        /// Wave Director run-start intent, carried across a cross-region
        /// arena warp the same way phantoms ride via PhantomIntents.
        /// Confirmed live: a cross-region transfer destroys the ENTIRE Game
        /// instance (Player/Avatar/WaveDirector state all go with it) —
        /// "Start Run" would successfully teleport the player to the arena,
        /// but the WaveDirector polling loop watching for arrival was left
        /// behind on the now-destroyed old Player object, so the region was
        /// never cleared and no waves ever spawned. Only set while the run
        /// is still in WaveState.WarpingToArena (i.e. the very first step of
        /// a run, before anything has spawned yet) — snapshotted by
        /// Player.SnapshotWaveRunForTransfer() at BeginRegionTransfer, read
        /// back by Player.RestoreWaveRunFromMigration() from the new
        /// Avatar's OnEnteredWorld once it's actually standing in the arena.
        /// </summary>
        public WaveRunIntent PendingWaveRun { get; set; }

        /// <summary>
        /// Trial of the Impossible — set right before TeleportToRegionFromWeb
        /// warps the player to their randomly chosen arena. Cross-region
        /// transfer destroys the Game instance the confirming Player object
        /// lives on (same story as PendingWaveRun above): without this, the
        /// warp succeeds but the "spawn the nemesis on arrival" intent was
        /// left behind on the now-destroyed old Player object and never
        /// fired. Snapshotted by Player.SnapshotTrialWarpForTransfer() at
        /// BeginRegionTransfer, consumed by
        /// Player.OnAvatarEnteredRegionForTrial() from the new Avatar's
        /// OnEnteredWorld once it's actually standing in the arena.
        /// </summary>
        public bool PendingTrialWarp { get; set; }

        /// <summary>
        /// Danger Room Endless Terminal — same shape as PendingTrialWarp, set
        /// right before TeleportToRegionFromWeb warps the player into the
        /// training arena after confirming the Coulson dialog. Snapshotted by
        /// Player.SnapshotDangerRoomEndlessWarpForTransfer() at
        /// BeginRegionTransfer, consumed by
        /// Player.OnAvatarEnteredRegionForDangerRoomEndless() from the new
        /// Avatar's OnEnteredWorld once it's actually standing in the arena.
        /// </summary>
        public bool PendingDangerRoomEndlessWarp { get; set; }

        /// <summary>
        /// Bounty Hunt (Player.BountyHunt.cs) — same shape as
        /// PendingTrialWarp, but the target can't be recomputed after
        /// landing the way Trial's arena/roster can (Trial just needs to
        /// know "warp was intentional" and rebuilds everything fresh via
        /// StartTrialGauntlet; Bounty Hunt needs the SPECIFIC nemesis and its
        /// ephemeral rank to still be known once the new Game instance
        /// exists), so those two values are snapshotted here too, not just
        /// the flag. Set right before
        /// TeleportToRegionFromWeb warps the player to a random arena.
        /// Snapshotted by Player.SnapshotBountyHuntForTransfer() at
        /// BeginRegionTransfer, consumed by
        /// Player.OnAvatarEnteredRegionForBountyHunt() from the new Avatar's
        /// OnEnteredWorld once it's actually standing in the arena.
        /// </summary>
        public bool PendingBountyHuntWarp { get; set; }
        public ulong BountyHuntHeroRef { get; set; }
        public int BountyHuntRank { get; set; }

        /// <summary>
        /// Which Bounty Board slot (0-5) the in-flight Bounty Hunt warp
        /// above was launched from, or -1 if this hunt was started against
        /// a personal Nemesis roster entry instead of a board slot. Needed
        /// on the far side of the cross-region hop so
        /// Player.OnBountyHuntEntityDead/OnBountyHuntLoss know which
        /// BountyBoard entry (if any) to resolve.
        /// </summary>
        public int BountyHuntBoardSlot { get; set; } = -1;

        /// <summary>
        /// Theme index the IN-FLIGHT hunt was launched under. Separate from
        /// BountyThemeIndex (which tracks the board itself) and required for
        /// the same reason BountyHuntBoardSlot is: the arrival handler runs in
        /// a NEW Game instance after the region transfer, so a plain Player
        /// field is already back to -1 by the time the arena is sterilized.
        /// Without this the themed arena repopulation and themed Phantom
        /// Requiem pool both silently no-op. Bounty Board mode only.
        /// </summary>
        public int BountyHuntThemeIndex { get; set; } = -1;

        /// <summary>
        /// RegionPrototypeId (as ulong) of the arena the player's last
        /// Bounty Hunt warp landed them in, or 0 if none yet. Excluded from
        /// the random pick on the NEXT hunt so two hunts in a row can't
        /// send the player back into a region that may not have fully torn
        /// down yet — confirmed live 2026-08-02 as a real complaint
        /// ("sent to a region I've already been to that hasn't reset").
        /// Persisted here (not a plain Player field) because a region
        /// transfer destroys/recreates the Player/Game instance, so this
        /// needs to survive the exact hop it's meant to inform the next
        /// pick after.
        /// </summary>
        public ulong LastBountyHuntRegionId { get; set; }

        /// <summary>
        /// Game-clock ms timestamp of the last Bounty Hunt warp start —
        /// enforces a minimum gap between consecutive hunts (see Player.
        /// BountyHunt.cs's BountyHuntMinIntervalMs). Persisted the same way
        /// as LastBountyHuntRegionId and for the same reason: a region
        /// transfer destroys/recreates the Player instance, and this needs
        /// to survive the exact hop it's cooling down after.
        /// </summary>
        public long LastBountyHuntStartMs { get; set; }

        /// <summary>
        /// The Bounty Board — 6 randomly-rolled nemeses shown at once,
        /// independent of the player's personal Nemesis roster/kill
        /// history. See Player.BountyBoard.cs.
        /// </summary>
        public List<BountyBoardEntry> BountyBoard { get; } = new();

        /// <summary>
        /// Index into BountyThemes.All for the board's current themed roll, or
        /// -1 for an untheme(d)/legacy board. Has to ride along with the board
        /// itself — the theme drives arena, costume, hazard powers and arena
        /// repopulation for every hunt launched off that board, so losing it
        /// on a region transfer would silently untheme an in-progress board.
        /// Bounty Board mode only. See Player.BountyThemes.cs.
        /// </summary>
        public int BountyThemeIndex { get; set; } = -1;

        public MigrationData() { }

        public List<(ulong, ulong)> GetOrCreatePropertyList(ulong entityDbId)
        {
            if (_properties.TryGetValue(entityDbId, out List<(ulong, ulong)> list) == false)
            {
                list = new();
                _properties.Add(entityDbId, list);
            }

            return list;
        }

        public void RemovePropertyList(ulong entityDbId)
        {
            _properties.Remove(entityDbId);
        }

        public void Reset()
        {
            IsInErrorState = false;
            SkipNextUpdate = false;

            IsFirstLoad = true;

            // Properties for summoned entities need to be migrated, and these have arbitrary runtime dbIds, so just clear everything.
            _properties.Clear();

            WorldView.Clear();
            MatchQueueStatus = null;
            CommunityStatus.Clear();
            PhantomIntents.Clear();
            Nemeses.Clear();
            PreferredPowers.Clear();
            CombatRangePrefs.Clear();
            PendingWaveRun = null;
            PendingTrialWarp = false;
            PendingDangerRoomEndlessWarp = false;
            PendingBountyHuntWarp = false;
            BountyHuntHeroRef = 0;
            BountyHuntRank = 0;
            BountyHuntBoardSlot = -1;
            BountyHuntThemeIndex = -1;
            LastBountyHuntRegionId = 0;
            LastBountyHuntStartMs = 0;
            BountyBoard.Clear();
            BountyThemeIndex = -1;
        }
    }

    /// <summary>
    /// One nemesis on a player's persistent revenge history — an enemy
    /// phantom hero that has killed them at least once. The list never
    /// auto-clears; a Banish from the app is the only way to remove an
    /// entry.
    ///
    /// * Active (Defeated == false): they've killed you and haven't been
    ///   put down since. Eligible for Rogue Encounter respawn with the full
    ///   HP/damage/star treatment for their current Rank.
    /// * Defeated (Defeated == true): you got your revenge on their last
    ///   incarnation. Rank is retained; still in the history but not picked
    ///   for future ambushes unless they re-kill you and reactivate.
    ///
    /// When a Defeated nemesis kills you again, they reactivate and Rank
    /// bumps one more step (up to NemesisMaxRank).
    /// </summary>
    public sealed class NemesisEntry
    {
        /// <summary>Hero PrototypeId as ulong.</summary>
        public ulong HeroRef;

        /// <summary>
        /// True when HeroRef is a real boss-tier AgentPrototype (Doctor Doom,
        /// Kraven, etc. — CuratedBossRoster) instead of a playable Avatar.
        /// Boss nemeses can't be respawned via SpawnNemesisPhantomHero (it
        /// hard-requires an AvatarPrototype) — revenge spawns for these go
        /// through the plain-Agent boss-spawn path instead. Added 2026-07-26.
        /// </summary>
        public bool IsBoss;

        /// <summary>Rank climbs on repeat deaths. 1..5, capped.</summary>
        public int Rank;

        /// <summary>Total number of times this nemesis has killed the player.</summary>
        public int Kills;

        /// <summary>Total number of times the player has taken revenge and killed this nemesis.</summary>
        public int RevengeKills;

        /// <summary>
        /// True after a successful revenge kill; false while active. Cleared
        /// automatically if they come back and kill the player again.
        /// </summary>
        public bool Defeated;

        /// <summary>
        /// The generated username of the phantom that last killed the
        /// player as this nemesis. Preserved so the next ambush uses the
        /// same recognizable name ("VenomousGhost042 has returned…").
        /// Never a real player's account name.
        /// </summary>
        public string LastKillerName;

        /// <summary>UTC millis of the most recent kill (by them, of you).</summary>
        public long LastKillMs;

        /// <summary>
        /// Number of times this nemesis has escaped after killing the player
        /// (rank 4/5 only — see Avatar.Nemesis.cs). Each escape adds +2%
        /// HealthMaxMult on top of the rank curve for their next spawn, so a
        /// nemesis you keep losing to gets progressively tankier instead of
        /// just standing there for an easy revenge kill.
        /// </summary>
        public int EscapeCount;

        /// <summary>Number of times this nemesis has been spared (SpareNemesis) instead of finished off normally.</summary>
        public int MercyCount;
    }

    /// <summary>
    /// One posting on the Bounty Board — a randomly-rolled nemesis shown
    /// to the player independent of their personal Nemesis roster/kill
    /// history. Up to 6 exist at once (Player.BountyBoard.cs); once every
    /// slot is Resolved (Defeated or Fled) the whole board rerolls fresh.
    /// </summary>
    public sealed class BountyBoardEntry
    {
        /// <summary>Hero PrototypeId as ulong.</summary>
        public ulong HeroRef;

        /// <summary>True when HeroRef is a curated boss AgentPrototype instead of a playable Avatar. See NemesisEntry.IsBoss.</summary>
        public bool IsBoss;

        /// <summary>Current difficulty, 1-10. Starts low on roll, climbs by 1 each time the player loses to this bounty (capped at 10).</summary>
        public int Rank;

        /// <summary>
        /// Losses to THIS specific bounty since it was rolled, 0-2. On the
        /// 3rd loss the bounty flees permanently (Fled = true) instead of
        /// ranking up again — the player loses the reward and the credits
        /// already spent to post it, same as any other loss.
        /// </summary>
        public int LossCount;

        /// <summary>True once the player has killed this bounty. Resolved (counts toward a board reroll) but stays visible until reroll.</summary>
        public bool Defeated;

        /// <summary>
        /// True once the player has claimed the currency (+ guaranteed BiS
        /// at rank 9-10) reward for a Defeated bounty via
        /// CollectBountyBoardReward. Killing a bounty only sets Defeated —
        /// the reward itself waits for this explicit "Collect Rewards"
        /// click rather than granting silently at kill time.
        /// </summary>
        public bool RewardCollected;

        /// <summary>True once this bounty has fled (3rd loss). Resolved, no longer huntable, stays visible until reroll.</summary>
        public bool Fled;

        /// <summary>Display name captured at spawn time, same purpose as NemesisEntry.LastKillerName.</summary>
        public string LastKillerName;

        /// <summary>
        /// The exact BiS piece this bounty is guaranteed to drop, rolled ONCE
        /// when the slot first reaches BountyBoardGuaranteedBisRank so the card
        /// can show the player which specific item they are hunting for and
        /// the drop matches it. 0 = none (boss slots, or below that rank).
        /// Rolled from the BOUNTY'S OWN hero loadout, not the player's - you
        /// kill Psylocke, you take a piece of Psylocke's kit.
        /// Bounty Board mode only.
        /// </summary>
        public ulong GuaranteedBisRef;
    }

    /// <summary>
    /// One phantom-hero the human wants to bring across a region transfer.
    /// AvatarRef is the PrototypeId as ulong (kept ulong to stay free of a
    /// GameData reference from this DatabaseAccess project).
    /// </summary>
    public sealed class PhantomIntent
    {
        public ulong AvatarRef;
        public int Level;
        public string Username;

        /// <summary>
        /// True when the phantom's level was explicitly locked by the user
        /// at spawn (e.g. `!phantom spawn 4 45`) — the tick loop's
        /// auto-level-with-caller sync must skip these phantoms so they
        /// stay at exactly the level the user asked for. False when the
        /// spawn used the default (match caller's level), so the auto-
        /// level tick keeps them chasing the human.
        /// </summary>
        public bool LockLevel;

        /// <summary>
        /// Costume PrototypeId as ulong (0 = roll a random costume at
        /// spawn). Stores the costume actually applied, so squad saves and
        /// cross-region transfers reproduce the same look rather than
        /// re-rolling.
        /// </summary>
        public ulong CostumeRef;

        /// <summary>
        /// Equipped item PrototypeIds as ulongs, in equip-slot iteration
        /// order (null/empty = roll random gear at spawn). Item affixes
        /// re-roll on restore; the item identities are preserved.
        /// </summary>
        public System.Collections.Generic.List<ulong> GearRefs;

        /// <summary>
        /// True when the phantom was spawned with the "invincible" opt-in
        /// (Squad Builder checkbox). Applies PropertyEnum.Invulnerable at
        /// spawn so hits do nothing. False = normal HP curve, killable,
        /// downed on 0 HP with the caller's phantoms and the caller
        /// himself as valid revive sources.
        /// </summary>
        public bool Invincible;

        /// <summary>
        /// True when this phantom was spawned with the party/raid size cap
        /// bypassed (Phantom Heroes / Squad Builder "Bypass party/raid size
        /// limit" checkbox). Carried across region transfers so a squad that
        /// exceeds the normal cap doesn't get silently truncated back down
        /// to 5/10 when RestorePhantomsFromMigration re-spawns it fresh in
        /// the new region instance.
        /// </summary>
        public bool BypassCap;
    }

    /// <summary>
    /// Mirrors Player.WaveDirector's StartWaveRun parameters using only
    /// primitives (ulong/int/bool/float) to stay free of a GameData
    /// reference from this DatabaseAccess project — same convention as
    /// PhantomIntent.
    /// </summary>
    public sealed class WaveRunIntent
    {
        public List<WaveDefIntent> Waves { get; } = new();
        public int IntermissionMs;
        public ulong ArenaRegionRef;
        public bool ClearArena;
        public bool Loop;
        public float CountScalePerWave;
        public int LevelBumpPerWave;
        public int RewardMode;
        public ulong RewardLootTableRef;

        // Endless Challenge state — must ride the transfer too, or an
        // in-progress Endless run silently demotes to a normal repeating
        // wave run the instant it warps to its arena (see IsEndlessMode
        // usage in Player.WaveDirector.cs's SnapshotWaveRunForTransfer).
        public bool IsEndlessMode;
        public int EndlessCycle;
        public int EndlessPeakRank;
        public string EndlessHeroName;
    }

    public sealed class WaveDefIntent
    {
        public List<WaveEntryIntent> Entries { get; } = new();
        public int? IntermissionMsOverride;
    }

    public sealed class WaveEntryIntent
    {
        public ulong AgentRef;
        public ulong HeroRef;
        public bool IsEnemyPhantom;
        public int Count;
        public int Level;
        public int Rank;
    }
}
