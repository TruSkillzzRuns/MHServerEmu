// GET /webapi/leaderboard/trial-global
//
// Cross-ACCOUNT "everyone's Trial of the Impossible runs" leaderboard —
// unlike /webapi/leaderboard (which is scoped to one logged-in player), this
// scans every registered account's saved leaderboard file directly off disk
// (Player.LoadLeaderboardFileForAccount), including accounts that aren't
// currently online, and ranks every TrialOfImpossible run together.
//
// Per-run stats (kills/deaths for that one attempt), not lifetime totals —
// each row is a single real attempt, same shape as the existing per-account
// leaderboard's TerminalRun/TrialOfImpossible entries.

using MHServerEmu.Core.Network.Web;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class TrialLeaderboardGlobalWebHandler : WebHandler
    {
        protected override Task Get(WebRequestContext context)
        {
            var playerNames = new Dictionary<ulong, string>();
            IDBManager.Instance.GetPlayerNames(playerNames);

            var rows = new List<(string PlayerName, Player.LeaderboardEntry Entry)>();
            foreach (var (accountId, playerName) in playerNames)
            {
                var entries = Player.LoadLeaderboardFileForAccount(accountId);
                foreach (var entry in entries)
                {
                    if (entry.Kind != Player.LeaderboardKind.TrialOfImpossible) continue;
                    rows.Add((playerName, entry));
                }
            }

            // Same ranking rule as the per-account board: completed runs
            // always outrank aborted ones, then fastest elapsed time wins.
            rows.Sort((a, b) =>
            {
                if (a.Entry.Completed != b.Entry.Completed) return a.Entry.Completed ? -1 : 1;
                return a.Entry.Value.CompareTo(b.Entry.Value);
            });

            var result = new List<object>(rows.Count);
            int rank = 0;
            foreach (var (playerName, entry) in rows)
            {
                rank++;
                var portraitCandidates = ResolveHeroPortraitCandidates(entry.HeroName);
                result.Add(new
                {
                    Rank = rank,
                    Id = entry.Id,
                    PlayerName = playerName,
                    HeroName = entry.HeroName,
                    HeroPortraitPath = portraitCandidates.Count > 0 ? portraitCandidates[0] : null,
                    HeroPortraitCandidates = portraitCandidates,
                    NemesisKills = entry.NemesisKills,
                    Deaths = entry.Deaths,
                    Completed = entry.Completed,
                    FailReason = entry.FailReason,
                    ElapsedMs = (long)entry.Value,
                    TimestampMs = entry.TimestampMs,
                });
            }

            return context.SendJsonAsync(new { Ok = true, Entries = result });
        }

        /// <summary>"Thor" -> every real portrait asset candidate for that playable avatar, resolved from loaded client data — same fallback chain PhantomsCatalogWebHandler uses for its hero list, since not every candidate lives in the TFC stream the portrait endpoint extracts from.</summary>
        private static List<string> ResolveHeroPortraitCandidates(string heroShortName)
        {
            var candidates = new List<string>(4);
            if (string.IsNullOrWhiteSpace(heroShortName)) return candidates;

            foreach (var (avatarRef, shortName) in Avatar.GetAllPhantomHeroRefs())
            {
                if (string.Equals(shortName, heroShortName, StringComparison.OrdinalIgnoreCase) == false) continue;

                var avatarProto = avatarRef.As<AvatarPrototype>();
                if (avatarProto == null) break;

                void AddCandidate(AssetId assetId)
                {
                    if (assetId == 0) return;
                    string assetName = GameDatabase.GetAssetName(assetId);
                    if (string.IsNullOrEmpty(assetName) == false && candidates.Contains(assetName) == false)
                        candidates.Add(assetName);
                }
                AddCandidate(avatarProto.PortraitPath);
                AddCandidate(avatarProto.CharacterSelectIconPortraitSmall);
                AddCandidate(avatarProto.SocialIconPath);
                AddCandidate(avatarProto.CharacterSelectIconPath);
                break;
            }

            return candidates;
        }
    }
}
