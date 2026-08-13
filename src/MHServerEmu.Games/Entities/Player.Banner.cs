using MHServerEmu.Core.Network;

namespace MHServerEmu.Games.Entities
{
    /// <summary>
    /// Chat banner primitives — pushing a line, or a divider-wrapped block,
    /// into this player's in-game chat.
    ///
    /// These are player-level capabilities, not feature behaviour: they need
    /// nothing but <see cref="PlayerConnection"/>. They lived in
    /// Player.RogueEncounter.cs purely by accident of which feature happened to
    /// need a banner first, which meant eleven other features — BountyBoard,
    /// BountyHunt, DangerRoom, four Deathmatch files, Nemesis, TrialOfImpossible
    /// and WaveDirector — had to reach into Rogue Encounter to say anything in
    /// chat. That single misplaced helper was the largest dependency edge in
    /// the codebase (9 borrowing features).
    ///
    /// Kept ON Player rather than moved to a feature class because that is what
    /// they genuinely are, and because it leaves all 38 call sites untouched.
    /// </summary>
    public partial class Player
    {
        private const string BannerDivider = "━━━━━━━━━━━━━━━━━━━━";

        /// <summary>
        /// Sends <paramref name="text"/> wrapped in divider lines, so an
        /// announcement stands out from ordinary combat spam.
        /// </summary>
        internal void SendBannerLines(string text)
        {
            if (PlayerConnection == null) return;

            SendBannerLine(BannerDivider);
            SendBannerLine(text);
            SendBannerLine(BannerDivider);
        }

        /// <summary>
        /// Sends a single unadorned line. Silently does nothing when the player
        /// has no connection — callers are game logic that shouldn't have to
        /// null-check before saying something.
        /// </summary>
        internal void SendBannerLine(string line)
        {
            if (PlayerConnection == null) return;

            var msg = new ServiceMessage.GroupingManagerMetagameMessage(PlayerConnection.PlayerDbId, line, showSender: false);
            ServerManager.Instance.SendMessageToService(GameServiceType.GroupingManager, msg);
        }
    }
}
