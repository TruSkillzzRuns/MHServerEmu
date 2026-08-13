using System.Collections.Generic;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Features
{
    /// <summary>
    /// Boss-pool and prototype-naming lookups.
    ///
    /// Extracted from Player.WaveDirector.cs (2026-08-12). All three are pure
    /// statics over prototype data with their own process-lifetime caches -
    /// they never touched player state - but living on Player meant BountyBoard,
    /// BountyHunt, RogueEncounter, PhantomHero, WebPhantom, Deathmatch,
    /// DeathmatchDiag, DeathmatchTeams and TrialOfImpossible all had to reach
    /// into WaveDirector for a name lookup or a widget ref.
    ///
    /// Call sites are unchanged: consumers add
    /// `using static MHServerEmu.Games.Features.BossPool;`.
    /// </summary>
    public static class BossPool
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        // Cached once per process: a UIWidgetGenericFractionPrototype the
        // loaded client data ships. What the fraction NUMBERS show is driven
        // entirely by SetCount()/SetTimeRemaining() below, but each instance
        // also carries its own baked, unrenamable Descriptor label — blindly
        // grabbing "the first one found" picked UI/MetaGame/MindlessTitan
        // .prototype, whose native label is "Defeat Mindless Titan N/M"
        // (confirmed live via /webapi/debug/uiwidgets — misleading even
        // though the fraction itself tracked correctly).
        //
        // TimeRemainingNoText (one of only 5/245 instances with a blank
        // Descriptor) was tried next and confirmed live to render as a
        // completely empty widget — "no text" apparently means "no display
        // at all" for this prototype, not just "no label", so it's unusable.
        //
        // UI/MetaGame/CurrentCountTotalCountOnly.prototype's own native
        // Descriptor is literally just "$CurrentCount$/$TotalCount$" — bare
        // numbers, no label text — confirmed via the same scan to be the
        // only real match for "just show the fraction." The old "take the
        // first one found" logic is kept only as a fallback in case this
        // specific ref doesn't resolve on some client data version.
        private const ulong PreferredGenericFractionWidgetRef = 0xA447BFC6DD2313EB; // UI/MetaGame/CurrentCountTotalCountOnly.prototype

        /// <summary>Leaf hero name for a nemesis display name — "Powers/Player/Thor/Thor.prototype" -> "Thor".</summary>
        internal static string LeafHeroName(PrototypeId avatarRef)
        {
            string path = GameDatabase.GetPrototypeName(avatarRef);
            if (string.IsNullOrEmpty(path)) return "Phantom";
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }
        private static PrototypeId? s_endlessWaveWidgetRef;
        private static bool s_endlessWaveWidgetSearched;

        internal static PrototypeId GetEndlessWaveWidgetRef()
        {
            if (s_endlessWaveWidgetSearched) return s_endlessWaveWidgetRef ?? PrototypeId.Invalid;
            s_endlessWaveWidgetSearched = true;

            if (((PrototypeId)PreferredGenericFractionWidgetRef).As<UIWidgetGenericFractionPrototype>() != null)
            {
                s_endlessWaveWidgetRef = (PrototypeId)PreferredGenericFractionWidgetRef;
                return s_endlessWaveWidgetRef.Value;
            }

            foreach (PrototypeId protoRef in DataDirectory.Instance
                .IteratePrototypesInHierarchy<UIWidgetGenericFractionPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                s_endlessWaveWidgetRef = protoRef;
                break;
            }
            return s_endlessWaveWidgetRef ?? PrototypeId.Invalid;
        }
        private static List<PrototypeId> s_rawBossCandidatePool;
        private static readonly object s_rawBossCandidatePoolLock = new();

        /// <summary>
        /// Every /Bosses/ AgentPrototype that passes the base filters (real
        /// icon, combat brain, not test/debug/raid/deprecated) — the
        /// uncurated ~687-entry pool CuratedBossRoster.SelectCanonical
        /// narrows down from. Split out of GetEndlessBossPool so
        /// Player.BountyBoard.cs can run its own SelectCanonicalWithNames
        /// pass over the same candidates without re-walking DataDirectory.
        /// </summary>
        internal static List<PrototypeId> GetRawBossCandidatePool()
        {
            if (s_rawBossCandidatePool != null) return s_rawBossCandidatePool;
            lock (s_rawBossCandidatePoolLock)
            {
                if (s_rawBossCandidatePool != null) return s_rawBossCandidatePool;

                var pool = new List<PrototypeId>(256);
                foreach (PrototypeId agentRef in DataDirectory.Instance.IteratePrototypesInHierarchy<AgentPrototype>(PrototypeIterateFlags.NoAbstract))
                {
                    if (agentRef == PrototypeId.Invalid) continue;
                    var proto = agentRef.As<AgentPrototype>();
                    if (proto == null || proto is AvatarPrototype) continue;

#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                    AssetId iconAssetId = proto.IconPathHiRes != 0 ? proto.IconPathHiRes : proto.IconPath;
#else
                    // AgentPrototype.IconPathHiRes doesn't exist under 1.48.
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
                        // Raid-exclusive bosses excluded per user request 2026-07-26.
                        || path.IndexOf("/SurturRaid/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/OnslaughtRaid/", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    // Individually excluded bosses — confirmed live 2026-08-08:
                    // SkrullNickFury (Entity/Characters/Bosses/SecretInvasion/)
                    // rolled as a Rogue Encounter "real boss ambush" and was
                    // effectively unkillable — a real hit's damage was measured
                    // dropping from 4758 (raw) to 60 (final) via instrumented
                    // logging, ~95% of that from his own intrinsic
                    // DamagePctResist (read directly at PowerPayload.cs:1782,
                    // baked into his own prototype data, not a property this
                    // fork's code ever sets). No scripted "remove the shield"
                    // mechanic was found anywhere in his AI profile or any
                    // Nick-Fury-named mission/power/condition prototype after
                    // an exhaustive search — whatever neutralizes it in his
                    // real Chapter 10 story encounter isn't something a
                    // standalone Rogue Encounter/Bounty Hunt/manual spawn can
                    // reproduce, so he's excluded from this pool entirely
                    // rather than guessing at a numeric override.
                    if (path.Equals("Entity/Characters/Bosses/SecretInvasion/SkrullNickFury.prototype", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (proto.BehaviorProfile == null || proto.BehaviorProfile.Brain == PrototypeId.Invalid) continue;

                    pool.Add(agentRef);
                }

                s_rawBossCandidatePool = pool;
                return pool;
            }
        }
    }
}
