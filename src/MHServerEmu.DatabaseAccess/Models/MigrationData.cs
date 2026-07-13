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
    }
}
