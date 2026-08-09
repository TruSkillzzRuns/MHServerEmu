using System;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.VectorMath;

namespace MHServerEmu.Games.Entities.Avatars
{
    /// <summary>
    /// Event-driven trigger hooks for the phantom hero AI — the
    /// event-driven-triggers half of a standard MMO/ARPG AI architecture
    /// (blackboard state + event-driven triggers), alongside the priority-
    /// selector hunt loop in Avatar.PhantomHero.cs. Other systems (AI
    /// decision logging, future difficulty-slider tooling, etc.) subscribe
    /// here instead of being wired directly into UpdatePhantomHunt's control
    /// flow. A built-in debug-log subscriber is registered below so the
    /// hooks are independently verifiable without needing separate tooling.
    /// </summary>
    public static class PhantomAIEvents
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public static event Action<Agent, WorldEntity> OnBossSpawn;
        public static event Action<Agent, WorldEntity> OnEliteSpawn;
        public static event Action<Avatar> OnPlayerLowHP;
        public static event Action<Agent> OnPhantomLowHP;
        public static event Action<Agent, Vector3> OnHazardDetected;

        static PhantomAIEvents()
        {
            // Built-in debug-log subscriber — proves every hook actually
            // fires and doubles as the start of the "AI decision logging"
            // developer QOL tooling. Toggle DebugLoggingEnabled off if this
            // gets noisy in a busy fight.
            OnBossSpawn += (phantom, boss) => { if (DebugLoggingEnabled) Logger.Info($"[PhantomAIEvent:BossSpawn] {phantom} sees boss {boss.PrototypeName}"); };
            OnEliteSpawn += (phantom, elite) => { if (DebugLoggingEnabled) Logger.Info($"[PhantomAIEvent:EliteSpawn] {phantom} sees elite {elite.PrototypeName}"); };
            OnPlayerLowHP += (avatar) => { if (DebugLoggingEnabled) Logger.Info($"[PhantomAIEvent:PlayerLowHP] {avatar.GetOwnerOfType<Player>()?.GetName() ?? avatar.ToString()}"); };
            OnPhantomLowHP += (phantom) => { if (DebugLoggingEnabled) Logger.Info($"[PhantomAIEvent:PhantomLowHP] {phantom}"); };
            OnHazardDetected += (phantom, pos) => { if (DebugLoggingEnabled) Logger.Info($"[PhantomAIEvent:HazardDetected] {phantom} at {pos.ToStringNames()}"); };
        }

        /// <summary>Off by default — this can fire often in a busy fight. Toggle for AI debugging.</summary>
        public static bool DebugLoggingEnabled { get; set; } = false;

        internal static void RaiseBossSpawn(Agent phantom, WorldEntity boss) => OnBossSpawn?.Invoke(phantom, boss);
        internal static void RaiseEliteSpawn(Agent phantom, WorldEntity elite) => OnEliteSpawn?.Invoke(phantom, elite);
        internal static void RaisePlayerLowHP(Avatar avatar) => OnPlayerLowHP?.Invoke(avatar);
        internal static void RaisePhantomLowHP(Agent phantom) => OnPhantomLowHP?.Invoke(phantom);
        internal static void RaiseHazardDetected(Agent phantom, Vector3 hazardPos) => OnHazardDetected?.Invoke(phantom, hazardPos);
    }
}
