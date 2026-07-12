using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.Games.Entities
{
    // OmegaDev2 Phantom Heroes bridge. Same shape as the Gear Picker bridge
    // (Player.WebItemGive.cs): HTTP handlers run on ThreadPool threads while
    // every phantom operation touches entity state and Game.Current, so each
    // web request marshals a delegate onto this player's game thread via a
    // zero-delay scheduled event and awaits the TaskCompletionSource.
    public partial class Player
    {
        private static readonly Logger WebPhantomLogger = LogManager.CreateLogger();

        public sealed class WebPhantomOpState
        {
            public Func<Player, object> Op { get; }
            public TaskCompletionSource<object> Tcs { get; }
            public WebPhantomOpState(Func<Player, object> op, TaskCompletionSource<object> tcs)
            {
                Op = op;
                Tcs = tcs;
            }
        }

        private readonly EventGroup _webPhantomEvents = new();

        /// <summary>
        /// Thread-safe entry point for the phantom web endpoints. Runs
        /// <paramref name="op"/> on this player's game thread and completes
        /// <paramref name="tcs"/> with whatever it returns. Each call gets
        /// its own event pointer so concurrent requests don't cancel each
        /// other.
        /// </summary>
        public void RunPhantomWebOp(Func<Player, object> op, TaskCompletionSource<object> tcs)
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null)
            {
                tcs.TrySetResult(new WebPhantomError { Ok = false, Error = "no game scheduler" });
                return;
            }

            EventPointer<WebPhantomOpEvent> eventPointer = new();
            scheduler.ScheduleEvent(eventPointer, TimeSpan.Zero, _webPhantomEvents);
            eventPointer.Get().Initialize(this, new WebPhantomOpState(op, tcs));
        }

        private void DoWebPhantomOp(WebPhantomOpState state)
        {
            try
            {
                state.Tcs.TrySetResult(state.Op(this));
            }
            catch (Exception ex)
            {
                WebPhantomLogger.Warn($"[Phantom:Web] op threw: {ex}");
                state.Tcs.TrySetResult(new WebPhantomError { Ok = false, Error = ex.Message });
            }
        }

        private sealed class WebPhantomOpEvent : CallMethodEventParam1<Player, WebPhantomOpState>
        {
            protected override CallbackDelegate GetCallback() => static (player, state) => player.DoWebPhantomOp(state);
        }

        public sealed class WebPhantomError
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
        }

        // ================================================================
        //  Structured views over private phantom state, for the web tool.
        //  Game-thread only — call from inside a RunPhantomWebOp delegate.
        // ================================================================

        public sealed class WebPhantomInfo
        {
            public string AvatarId { get; set; }
            public string HeroProtoRef { get; set; }
            public string HeroName { get; set; }
            public string Username { get; set; }
            public int Level { get; set; }
            public bool LockLevel { get; set; }
            public string CostumeRef { get; set; }
            public bool InWorld { get; set; }
        }

        public List<WebPhantomInfo> GetPhantomInfosForWeb()
        {
            var list = new List<WebPhantomInfo>(_phantomAvatarIds.Count);
            var mgr = Game?.EntityManager;

            for (int i = 0; i < _phantomAvatarIds.Count; i++)
            {
                var d = _phantomDescriptors[i];
                Avatar av = mgr?.GetEntity<Avatar>(_phantomAvatarIds[i]);

                PrototypeId heroRef = av != null ? av.PrototypeDataRef : (PrototypeId)d.AvatarRef;
                list.Add(new WebPhantomInfo
                {
                    AvatarId = $"0x{_phantomAvatarIds[i]:X}",
                    HeroProtoRef = $"0x{(ulong)heroRef:X16}",
                    HeroName = WebLeafOf(GameDatabase.GetPrototypeName(heroRef)),
                    Username = d.Username,
                    Level = av?.CharacterLevel ?? d.Level,
                    LockLevel = d.LockLevel,
                    CostumeRef = d.CostumeRef != 0 ? $"0x{d.CostumeRef:X16}" : null,
                    InWorld = av?.IsInWorld == true,
                });
            }

            return list;
        }

        public sealed class WebEnemyPhantomInfo
        {
            public string HeroName { get; set; }
            public int Level { get; set; }
            public int HealthPct { get; set; }
            public bool Dead { get; set; }
        }

        public List<WebEnemyPhantomInfo> GetEnemyPhantomInfosForWeb()
        {
            var list = new List<WebEnemyPhantomInfo>(_enemyPhantomAvatarIds.Count);
            var mgr = Game?.EntityManager;
            if (mgr == null) return list;

            foreach (ulong avatarId in _enemyPhantomAvatarIds)
            {
                var av = mgr.GetEntity<Avatar>(avatarId);
                if (av == null) continue;

                long health = av.Properties[MHServerEmu.Games.Properties.PropertyEnum.Health];
                long healthMax = av.Properties[MHServerEmu.Games.Properties.PropertyEnum.HealthMax];

                list.Add(new WebEnemyPhantomInfo
                {
                    HeroName = WebLeafOf(GameDatabase.GetPrototypeName(av.PrototypeDataRef)),
                    Level = av.CharacterLevel,
                    HealthPct = healthMax > 0 ? (int)(health * 100 / healthMax) : 0,
                    Dead = av.IsDead,
                });
            }

            return list;
        }

        public sealed class WebPhantomSquadInfo
        {
            public string Name { get; set; }
            public List<string> Heroes { get; set; }
            public List<int> Levels { get; set; }
            public List<WebPhantomSquadMember> Members { get; set; }
        }

        public sealed class WebPhantomSquadMember
        {
            public string AvatarRef { get; set; }
            public string HeroName { get; set; }
            public int Level { get; set; }
            public bool LockLevel { get; set; }
            public string CostumeRef { get; set; }
            public bool Invincible { get; set; }
        }

        public List<WebPhantomSquadInfo> GetPhantomSquadsForWeb()
        {
            var squads = LoadPhantomSquadFile();
            var list = new List<WebPhantomSquadInfo>(squads.Count);
            foreach (var kvp in squads)
            {
                var heroes = new List<string>(kvp.Value.Count);
                var levels = new List<int>(kvp.Value.Count);
                var members = new List<WebPhantomSquadMember>(kvp.Value.Count);
                foreach (var m in kvp.Value)
                {
                    string heroName = WebLeafOf(GameDatabase.GetPrototypeName((PrototypeId)m.AvatarRef));
                    heroes.Add(heroName);
                    levels.Add(m.LockLevel ? m.Level : 0); // 0 = auto-level
                    members.Add(new WebPhantomSquadMember
                    {
                        AvatarRef = $"0x{m.AvatarRef:X16}",
                        HeroName = heroName,
                        Level = m.Level,
                        LockLevel = m.LockLevel,
                        CostumeRef = m.CostumeRef != 0 ? $"0x{m.CostumeRef:X16}" : null,
                        Invincible = m.Invincible,
                    });
                }
                list.Add(new WebPhantomSquadInfo { Name = kvp.Key, Heroes = heroes, Levels = levels, Members = members });
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public sealed class WebSquadSaveMember
        {
            public ulong AvatarRef { get; set; }
            public int Level { get; set; }
            public bool LockLevel { get; set; }
            public ulong CostumeRef { get; set; }
            public bool Invincible { get; set; }
        }

        /// <summary>
        /// Save a squad from an explicit member list (the OmegaDev2 visual
        /// Squad Builder) rather than a snapshot of the live phantoms. Same
        /// file, same name rules, same limits as SavePhantomSquad — usernames
        /// and gear are left null so they're minted/rolled fresh at spawn.
        /// </summary>
        public string SavePhantomSquadFromList(string squadName, List<WebSquadSaveMember> members)
        {
            if (IsValidSquadName(squadName) == false)
                return "Squad names must be 1-32 letters, digits, _ or -.";
            if (members == null || members.Count == 0)
                return "Squad has no members.";
            if (members.Count > 50)
                return "Squad too large (max 50).";

            foreach (var m in members)
                if (((PrototypeId)m.AvatarRef).As<GameData.Prototypes.AvatarPrototype>() == null)
                    return $"0x{m.AvatarRef:X16} is not an avatar prototype.";

            var squads = LoadPhantomSquadFile();
            if (squads.ContainsKey(squadName) == false && squads.Count >= PhantomSquadMaxCount)
                return $"Squad limit reached ({PhantomSquadMaxCount}). Delete one first.";

            var stored = new List<PhantomSquadMember>(members.Count);
            foreach (var m in members)
            {
                stored.Add(new PhantomSquadMember
                {
                    AvatarRef = m.AvatarRef,
                    Level = Math.Clamp(m.Level, 0, 60),
                    Username = null,
                    LockLevel = m.LockLevel && m.Level > 0,
                    CostumeRef = m.CostumeRef,
                    GearRefs = null,
                    Invincible = m.Invincible,
                });
            }

            squads[squadName] = stored;
            if (SavePhantomSquadFile(squads) == false)
                return "Failed to write squad file — check server log.";
            return $"Squad '{squadName}' saved ({stored.Count} phantom(s)).";
        }

        private static string WebLeafOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }
    }
}
