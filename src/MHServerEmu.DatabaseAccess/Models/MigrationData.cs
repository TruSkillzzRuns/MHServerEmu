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
        }
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
    }
}
