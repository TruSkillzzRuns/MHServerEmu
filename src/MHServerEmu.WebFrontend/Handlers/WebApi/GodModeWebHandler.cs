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
//   it's on. These properties have no auto-expiration (confirmed: a real
//   Property write, not a timed Condition), so a toggle left on from an
//   earlier session silently persists across zone changes/logout until
//   explicitly reset — this endpoint is what makes that state visible.

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

    public class GodModeWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
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

                if (flags.HasFlag(GodModeFlags.Invulnerable))
                {
                    props[PropertyEnum.Invulnerable] = invulnerable;
                    changes.Add($"invuln={invulnerable}");
                }

                if (flags.HasFlag(GodModeFlags.NoEnduranceCosts))
                {
                    props[PropertyEnum.NoEnduranceCosts, (int)ManaType.Type1] = noEnduranceCosts;
                    props[PropertyEnum.NoEnduranceCosts, (int)ManaType.Type2] = noEnduranceCosts;
                    props[PropertyEnum.NoEnduranceCosts, (int)ManaType.TypeAll] = noEnduranceCosts;
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
                    float pct = noCooldowns ? -0.999f : 0.0f;
                    props[PropertyEnum.CooldownModifierPctGlobal] = pct;
                    changes.Add($"noCooldowns={noCooldowns}");
                }

                if (flags.HasFlag(GodModeFlags.DamageMult))
                {
                    float wantMult = Math.Clamp(damageMult, 1.0f, 10000.0f);
                    // DamagePctBonus is a percent bonus, not a raw multiplier:
                    // out = base * (1 + DamagePctBonus / 100).
                    float dmg = Math.Max(0f, (wantMult - 1.0f) * 100.0f);
                    props[PropertyEnum.DamagePctBonus] = dmg;
                    changes.Add($"damageMult={wantMult}x");
                }

                if (flags.HasFlag(GodModeFlags.SpeedMult))
                {
                    float wantMult = Math.Clamp(speedMult, 0.1f, 20.0f);
                    float baseSpeed = avatar.Locomotor?.DefaultRunSpeed ?? 400.0f;
                    // Override gives an immediate speed bump; Rate survives the
                    // moment a power's Condition resets Override back to 0.
                    props[PropertyEnum.MovementSpeedOverride] = baseSpeed * wantMult;
                    props[PropertyEnum.MovementSpeedRate] = wantMult;
                    changes.Add($"speedMult={wantMult}x");
                }

                // Read back from the avatar itself so the snapshot always
                // reflects what's actually live, not just what we intended.
                bool snapInvuln = props[PropertyEnum.Invulnerable];
                bool snapNoEnd = props[PropertyEnum.NoEnduranceCosts, (int)ManaType.TypeAll];
                float cdPct = props[PropertyEnum.CooldownModifierPctGlobal];
                bool snapNoCd = cdPct <= -0.99f;
                float dmgPct = props[PropertyEnum.DamagePctBonus];
                float snapDamageMult = 1.0f + (dmgPct / 100.0f);
                float spdOverride = props[PropertyEnum.MovementSpeedOverride];
                float baseSp = avatar.Locomotor?.DefaultRunSpeed ?? 400.0f;
                float snapSpeedMult = spdOverride > 0f && baseSp > 0f ? spdOverride / baseSp : 1.0f;

                if (changes.Count > 0)
                    Logger.Info($"[GodMode] {p.GetName()}: {string.Join(", ", changes)}");

                return new
                {
                    Ok = true,
                    Message = changes.Count > 0 ? string.Join(", ", changes) : "no changes",
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

    public class GodModeStatusWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
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
                float dmgPct = props[PropertyEnum.DamagePctBonus];
                float snapDamageMult = 1.0f + (dmgPct / 100.0f);
                float spdOverride = props[PropertyEnum.MovementSpeedOverride];
                float baseSp = avatar.Locomotor?.DefaultRunSpeed ?? 400.0f;
                float snapSpeedMult = spdOverride > 0f && baseSp > 0f ? spdOverride / baseSp : 1.0f;

                bool anyActive = snapInvuln || snapNoEnd || snapNoCd || snapDamageMult > 1.01f || snapSpeedMult > 1.01f;

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
