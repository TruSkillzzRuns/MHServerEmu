// God Mode — runtime player buff cabinet, ported from the standalone OmegaDev
// tool's GodTier feature (tools\OmegaDev\OmegaDev.WinUI\Pages\GodTierPage.*)
// onto this fork's simpler single-process WebApi pattern (no service-message
// IPC hop — straight to PhantomsWebUtil.RunOnGameThread like every other
// OmegaDev2 endpoint).
//
//   POST /webapi/playeradmin/godmode
//     body: { playerName, flags, invulnerable, noEnduranceCosts, noCooldowns,
//             damageMult, speedMult }
//   `flags` is a GodModeFlags bitmask — only fields whose bit is set get
//   applied, so a single slider drag doesn't clobber the other toggles.
//   Response echoes a read-back snapshot so the UI can always resync to what
//   the server actually has, even if multiple clients touch it at once.
//
//   GET /webapi/playeradmin/godmode/status?player=...
//   Read-only snapshot, no writes — lets the app show a persistent
//   "God Mode active" indicator without the operator needing to remember
//   it's on.
//
// IMPORTANT — DamagePctBonus and MovementSpeedOverride/MovementSpeedRate are
// NOT God-Mode-exclusive properties. DamagePctBonus is the same aggregate
// stat real gear/passives contribute to (PowerPayload.cs sums
// power.Properties + ownerProperties for it directly in damage calc), and
// MovementSpeedRate/Override are the same properties real Sprint/dash/travel
// powers drive (Locomotor.cs). The original implementation overwrote these
// outright, which (a) destroyed whatever real bonus the player's own gear
// was contributing the moment God Mode touched them, including on Reset
// (which hard-set DamagePctBonus to exactly 0, erasing real gear bonus), and
// (b) made the "is God Mode active" check unreliable, since a player's own
// real gear-driven DamagePctBonus can naturally exceed the "active" threshold
// on its own (confirmed live 2026-07-19: the nav-pane banner showed ACTIVE on
// a fresh app launch with every God Mode toggle genuinely off, because the
// player's real equipped gear pushed DamagePctBonus above the threshold).
//
// Fixed via delta tracking: GodModeIntentTracker remembers exactly how much
// WE (God Mode) most recently added to each affected property, per avatar.
// Every apply first subtracts our own previous contribution back out (to
// recover whatever real/gear baseline is currently there), then adds the
// newly requested contribution — so a real gear change happening independently
// between two God Mode calls, or Reset, only ever removes what we introduced.
// "Active" status is now read from the tracker's own recorded intent, not
// from the shared properties, so real gear/mode state can never
// false-positive the indicator.

using System;
using System.Collections.Generic;
using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    [Flags]
    public enum GodModeFlags
    {
        None = 0,
        Invulnerable = 1 << 0,
        NoEnduranceCosts = 1 << 1,
        NoCooldowns = 1 << 2,
        DamageMult = 1 << 3,
        SpeedMult = 1 << 4,
        All = Invulnerable | NoEnduranceCosts | NoCooldowns | DamageMult | SpeedMult,
    }

    /// <summary>
    /// Per-avatar record of exactly what God Mode itself most recently
    /// intended/applied, kept in memory (not persisted, not part of the
    /// avatar's real PropertyCollection) so we never have to infer "is God
    /// Mode on" from properties real gameplay content can also set.
    /// </summary>
    internal sealed class GodModeIntent
    {
        public bool Invulnerable;
        public bool NoEnduranceCosts;
        public bool NoCooldowns;
        public float DamageMult = 1.0f;
        public float SpeedMult = 1.0f;

        // Exactly what we last wrote into the shared properties, so the next
        // apply can subtract it back out before adding the new amount.
        public float AppliedDamagePctDelta;
        public float AppliedSpeedRateDelta;

        public bool AnyActive =>
            Invulnerable || NoEnduranceCosts || NoCooldowns || DamageMult > 1.01f || SpeedMult > 1.01f;
    }

    internal static class GodModeTracker
    {
        private static readonly Dictionary<ulong, GodModeIntent> s_intents = new();

        public static GodModeIntent GetOrCreate(ulong avatarId)
        {
            if (s_intents.TryGetValue(avatarId, out GodModeIntent intent) == false)
            {
                intent = new GodModeIntent();
                s_intents[avatarId] = intent;
            }
            return intent;
        }

        public static GodModeIntent Peek(ulong avatarId)
        {
            return s_intents.TryGetValue(avatarId, out GodModeIntent intent) ? intent : null;
        }
    }

    public class GodModeWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();

            string playerName = null;
            int flagsRaw = 0;
            bool invulnerable = false, noEnduranceCosts = false, noCooldowns = false;
            float damageMult = 1.0f, speedMult = 1.0f;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("flags", out var f)) flagsRaw = f.GetInt32();
                if (root.TryGetProperty("invulnerable", out var iv)) invulnerable = iv.GetBoolean();
                if (root.TryGetProperty("noEnduranceCosts", out var ne)) noEnduranceCosts = ne.GetBoolean();
                if (root.TryGetProperty("noCooldowns", out var nc)) noCooldowns = nc.GetBoolean();
                if (root.TryGetProperty("damageMult", out var dm)) damageMult = (float)dm.GetDouble();
                if (root.TryGetProperty("speedMult", out var sm)) speedMult = (float)sm.GetDouble();
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            var flags = (GodModeFlags)flagsRaw;

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                Avatar avatar = p.CurrentAvatar;
                if (avatar == null || avatar.IsInWorld == false)
                    return new { Ok = false, Error = "no avatar in world" };

                var props = avatar.Properties;
                var changes = new List<string>();
                GodModeIntent intent = GodModeTracker.GetOrCreate(avatar.Id);

                if (flags.HasFlag(GodModeFlags.Invulnerable))
                {
                    props[PropertyEnum.Invulnerable] = invulnerable;
                    intent.Invulnerable = invulnerable;
                    changes.Add($"invuln={invulnerable}");
                }

                if (flags.HasFlag(GodModeFlags.NoEnduranceCosts))
                {
                    // Cheat-only property (no real content writer found) — plain
                    // overwrite is safe here.
                    props[PropertyEnum.NoEnduranceCosts, (int)ManaType.Type1] = noEnduranceCosts;
                    props[PropertyEnum.NoEnduranceCosts, (int)ManaType.Type2] = noEnduranceCosts;
                    props[PropertyEnum.NoEnduranceCosts, (int)ManaType.TypeAll] = noEnduranceCosts;
                    intent.NoEnduranceCosts = noEnduranceCosts;
                    changes.Add($"noEnduranceCosts={noEnduranceCosts}");
                }

                if (flags.HasFlag(GodModeFlags.NoCooldowns))
                {
                    // PropertyEnum.NoCooldowns has zero real consumers. The actual
                    // lever is CooldownModifierPctGlobal (Power.cs applies it as a
                    // multiplier, and only schedules the recharge callback when the
                    // resulting duration is > 0). -0.999 keeps cooldowns just barely
                    // above zero so charges keep regenerating, but the wait is
                    // effectively instant — a true 0 would freeze charge regen.
                    // Also cheat-only (no real content writer found) — plain
                    // overwrite is safe here.
                    float pct = noCooldowns ? -0.999f : 0.0f;
                    props[PropertyEnum.CooldownModifierPctGlobal] = pct;
                    intent.NoCooldowns = noCooldowns;
                    changes.Add($"noCooldowns={noCooldowns}");
                }

                if (flags.HasFlag(GodModeFlags.DamageMult))
                {
                    float wantMult = Math.Clamp(damageMult, 1.0f, 10000.0f);
                    // DamagePctBonus is a percent bonus, not a raw multiplier:
                    // out = base * (1 + DamagePctBonus / 100). It's ALSO the
                    // same property real gear/passives contribute to (see file
                    // header) — apply as a delta on top of whatever's
                    // currently there minus our own last contribution, so a
                    // real gear bonus is preserved instead of clobbered.
                    float newDelta = Math.Max(0f, (wantMult - 1.0f) * 100.0f);
                    float current = props[PropertyEnum.DamagePctBonus];
                    float baseline = current - intent.AppliedDamagePctDelta;
                    props[PropertyEnum.DamagePctBonus] = baseline + newDelta;
                    intent.AppliedDamagePctDelta = newDelta;
                    intent.DamageMult = wantMult;
                    changes.Add($"damageMult={wantMult}x");
                }

                if (flags.HasFlag(GodModeFlags.SpeedMult))
                {
                    float wantMult = Math.Clamp(speedMult, 0.1f, 20.0f);

                    // Locomotor.GetCurrentSpeed() branches on
                    // MovementSpeedOverride > 0 — when set (to ANY positive
                    // value, including exactly base speed), it pins speed to
                    // that number outright and skips the normal
                    // BaseMoveSpeed * MovementSpeedRate path entirely. That
                    // means simply writing "1.0x" here (baseSpeed * 1.0)
                    // wasn't a real no-op: it left the override property set,
                    // which permanently locked out any power-driven speed
                    // stacking (Sprint, dash charges, travel powers) even
                    // after God Mode was "reset". At 1.0x we fully remove the
                    // override so the Locomotor falls back to its normal,
                    // power-stacking-aware code path.
                    //
                    // MovementSpeedRate is ALSO a property real Sprint/dash/
                    // travel powers drive (Locomotor.cs) — same delta
                    // treatment as DamagePctBonus so we don't clobber it.
                    if (Math.Abs(wantMult - 1.0f) < 0.001f)
                    {
                        props.RemovePropertyRange(PropertyEnum.MovementSpeedOverride);
                        float currentRate = props[PropertyEnum.MovementSpeedRate];
                        props[PropertyEnum.MovementSpeedRate] = currentRate - intent.AppliedSpeedRateDelta;
                        intent.AppliedSpeedRateDelta = 0f;
                    }
                    else
                    {
                        float baseSpeed = avatar.Locomotor?.DefaultRunSpeed ?? 400.0f;
                        // Override gives an immediate speed bump; Rate survives the
                        // moment a power's Condition resets Override back to 0.
                        props[PropertyEnum.MovementSpeedOverride] = baseSpeed * wantMult;

                        float newRateDelta = wantMult - 1.0f;
                        float currentRate = props[PropertyEnum.MovementSpeedRate];
                        float rateBaseline = currentRate - intent.AppliedSpeedRateDelta;
                        props[PropertyEnum.MovementSpeedRate] = rateBaseline + newRateDelta;
                        intent.AppliedSpeedRateDelta = newRateDelta;
                    }
                    intent.SpeedMult = wantMult;
                    changes.Add($"speedMult={wantMult}x");
                }

                if (changes.Count > 0)
                    Logger.Info($"[GodMode] {p.GetName()}: {string.Join(", ", changes)}");

                // Invulnerable/NoEnduranceCosts/NoCooldowns are read straight
                // off the avatar (not the tracker) for the snapshot: unlike
                // damage/speed they're either cheat-only properties (no real
                // content ever legitimately sets NoEnduranceCosts or
                // CooldownModifierPctGlobal) or, for Invulnerable, worth
                // surfacing even if PvP mode set it — that's the original
                // "catch a toggle left on from an earlier session, even
                // across a server restart that wiped our in-memory tracker"
                // behavior, which is still safe and valuable for these three.
                bool snapInvuln = props[PropertyEnum.Invulnerable];
                bool snapNoEnd = props[PropertyEnum.NoEnduranceCosts, (int)ManaType.TypeAll];
                float cdPct = props[PropertyEnum.CooldownModifierPctGlobal];
                bool snapNoCd = cdPct <= -0.99f;

                return new
                {
                    Ok = true,
                    Message = changes.Count > 0 ? string.Join(", ", changes) : "no changes",
                    Snapshot = new
                    {
                        invulnerable = snapInvuln,
                        noEnduranceCosts = snapNoEnd,
                        noCooldowns = snapNoCd,
                        damageMult = intent.DamageMult,
                        speedMult = intent.SpeedMult,
                    },
                };
            });

            await context.SendJsonAsync(result);
        }
    }

    public class GodModeStatusWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string playerName = PhantomsWebUtil.QueryParam(context, "player");
            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                Avatar avatar = p.CurrentAvatar;
                if (avatar == null || avatar.IsInWorld == false)
                    return new { Ok = false, Error = "no avatar in world" };

                var props = avatar.Properties;
                bool snapInvuln = props[PropertyEnum.Invulnerable];
                bool snapNoEnd = props[PropertyEnum.NoEnduranceCosts, (int)ManaType.TypeAll];
                float cdPct = props[PropertyEnum.CooldownModifierPctGlobal];
                bool snapNoCd = cdPct <= -0.99f;

                // Damage/speed multipliers come from our own tracked intent,
                // NOT the shared properties — DamagePctBonus/MovementSpeedRate
                // can be nonzero purely from the player's own real gear,
                // which would otherwise false-positive this status
                // (confirmed live 2026-07-19: the banner showed ACTIVE on a
                // fresh app launch with every God Mode toggle genuinely off,
                // because equipped gear alone pushed DamagePctBonus above the
                // threshold). An avatar we've never touched this session has
                // no tracked intent at all, which correctly reads as 1.0x.
                GodModeIntent intent = GodModeTracker.Peek(avatar.Id);
                float snapDamageMult = intent?.DamageMult ?? 1.0f;
                float snapSpeedMult = intent?.SpeedMult ?? 1.0f;

                // Active is ALSO driven purely by tracked intent, not the raw
                // Invulnerable/CooldownModifierPctGlobal reads above (those
                // stay in Snapshot for informational purposes only). Real
                // content can flip both transiently and legitimately —
                // PvPDefenderGameMode sets Invulnerable directly on the
                // player avatar during defender objectives, and any hero
                // power/ultimate that grants a temporary "cooldowns reset"
                // effect would push CooldownModifierPctGlobal past our
                // threshold on its own. Reading either of those for the
                // banner flickered it on/off during ordinary play with zero
                // God Mode POSTs happening (confirmed live 2026-07-19: no
                // [GodMode] log lines at all while the banner kept flipping).
                // Tracked intent only reflects what an operator explicitly
                // asked for via this endpoint, so it can't be tripped by
                // anything else in the game.
                bool anyActive = intent?.AnyActive ?? false;

                return new
                {
                    Ok = true,
                    Player = p.GetName(),
                    Active = anyActive,
                    Snapshot = new
                    {
                        invulnerable = snapInvuln,
                        noEnduranceCosts = snapNoEnd,
                        noCooldowns = snapNoCd,
                        damageMult = snapDamageMult,
                        speedMult = snapSpeedMult,
                    },
                };
            });

            await context.SendJsonAsync(result);
        }
    }
}
