namespace MHServerEmu.Games.Entities.Avatars
{
    // Bridge between Avatar.OnKilled and Player.Nemesis. Isolated here so
    // the OnKilled hook is one call and the phantom / owner detection logic
    // lives with the phantom code rather than in Avatar's core.
    public partial class Avatar
    {
        /// <summary>
        /// If this avatar is a real player's and the killer was an ENEMY
        /// phantom hero (not a friendly phantom, not a mob), record the kill
        /// on the player's nemesis roster. No-op in every other case.
        /// </summary>
        internal void TryRegisterNemesisKill(WorldEntity killer, WorldEntity directKiller)
        {
            // Phantom deaths (both friendly and enemy) never add nemeses —
            // the roster is specifically the human's revenge list.
            if (IsPhantomHero) return;

            Player victimPlayer = GetOwnerOfType<Player>();
            if (victimPlayer == null || victimPlayer.PlayerConnection == null) return;

            Avatar killerAvatar = ResolveNemesisKillerAvatar(killer, directKiller);
            if (killerAvatar == null) return;

            victimPlayer.RegisterNemesisKill(killerAvatar);
        }

        /// <summary>
        /// Walk the killer / directKiller chain and return the first Avatar
        /// that is an ENEMY phantom (phantom hero with no PhantomCreatorId
        /// on its owning Player). Returns null if the kill was mob-driven or
        /// caused by a friendly phantom.
        /// </summary>
        private static Avatar ResolveNemesisKillerAvatar(WorldEntity killer, WorldEntity directKiller)
        {
            if (IsEnemyPhantomKiller(killer) is Avatar a) return a;
            if (IsEnemyPhantomKiller(directKiller) is Avatar b) return b;
            return null;
        }

        private static Avatar IsEnemyPhantomKiller(WorldEntity we)
        {
            if (we is not Avatar av) return null;
            if (av.IsPhantomHero == false) return null;
            Player owner = av.GetOwnerOfType<Player>();
            if (owner == null) return null;
            // Friendly phantoms carry PhantomCreatorId pointing at the human
            // that spawned them; enemy phantoms deliberately leave it 0.
            if (owner.PhantomCreatorId != 0) return null;
            return av;
        }
    }
}
