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

            Agent killerAgent = ResolveNemesisKillerAgent(killer, directKiller);
            if (killerAgent == null) return;

            var entry = victimPlayer.RegisterNemesisKill(killerAgent);

            // Rank 4/5 escape: instead of the nemesis just standing there
            // for an easy revenge kill once the player revives, they vanish
            // immediately and get 2% tankier for next time. Makes the
            // eventual kill against a nemesis you've lost to repeatedly
            // actually feel earned, since rank 4/5 already carry the best
            // loot tiers.
            if (entry != null && entry.Rank >= 4)
            {
                entry.EscapeCount++;
                victimPlayer.AnnounceNemesisEscape(entry.LastKillerName);
                Avatar.EscapeEnemyPhantom(killerAgent, victimPlayer);

                // More than 3 escapes at rank 4/5 (i.e. the 4th+ time this
                // nemesis has beaten the player while already at their
                // toughest/best-loot tier) — reset them back to rank 0.
                // They stop being a real threat/reward until they climb the
                // ranks again from scratch; this is the "miss out on the
                // good gear" consequence of never actually beating them.
                if (entry.EscapeCount > 3)
                {
                    entry.Rank = 0;
                    entry.EscapeCount = 0;
                    victimPlayer.AnnounceNemesisRankReset(entry.LastKillerName);
                }
            }
        }

        /// <summary>
        /// Walk the killer / directKiller chain and return the first Agent
        /// that is an ENEMY phantom — Avatar phantom OR team-up phantom.
        /// Returns null if the kill was mob-driven or caused by a friendly
        /// phantom.
        /// </summary>
        private static Agent ResolveNemesisKillerAgent(WorldEntity killer, WorldEntity directKiller)
        {
            if (IsEnemyPhantomKiller(killer) is Agent a) return a;
            if (IsEnemyPhantomKiller(directKiller) is Agent b) return b;
            return null;
        }

        private static Agent IsEnemyPhantomKiller(WorldEntity we)
        {
            if (we is not Agent ag) return null;
            if (ag.IsPhantomHero == false) return null;
            Player owner = ag.GetOwnerOfType<Player>();
            if (owner == null) return null;
            // Friendly phantoms carry PhantomCreatorId pointing at the human
            // that spawned them; enemy phantoms deliberately leave it 0.
            if (owner.PhantomCreatorId != 0) return null;
            return ag;
        }
    }
}
