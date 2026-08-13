using System;
using System.Collections.Generic;
using System.Text;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.MetaGames;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;
using MHServerEmu.Games.UI.Widgets;
using static MHServerEmu.Games.Features.BossPool;

namespace MHServerEmu.Games.Entities
{
    // One-shot deathmatch self-check.
    //
    // Written because diagnosing this mode one symptom at a time was costing a
    // full test round-trip per question. This dumps every value the mode depends
    // on in a single block, a few seconds after the arena is built, so a single
    // run answers: did the patches load, did the metagame and its mode activate,
    // are the alliances right, did the spawns land where intended, and is the
    // arena actually empty.
    //
    // Each line is prefixed [DMDIAG] and flagged OK / BAD so failures can be
    // grepped straight out of the log without reading the whole thing.
    public partial class Player
    {
        private readonly EventPointer<DeathmatchDiagEvent> _deathmatchDiag = new();

        /// <summary>Runs the self-check ~4s after setup, once the arena has settled.</summary>
        private void ScheduleDeathmatchDiagnostics()
        {
            if (_deathmatchDiag.IsValid) return;
            Game.GameEventScheduler?.ScheduleEvent(_deathmatchDiag, TimeSpan.FromMilliseconds(4000), _deathmatchEvents);
            _deathmatchDiag.Get()?.Initialize(this);
        }

        private static void Line(StringBuilder sb, bool ok, string label, object value, string note = null)
        {
            sb.Append("[DMDIAG] ").Append(ok ? "OK  " : "BAD ").Append(label.PadRight(30)).Append(value);
            if (string.IsNullOrEmpty(note) == false) sb.Append("   <- ").Append(note);
            sb.AppendLine();
        }

        private void DumpDeathmatchDiagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("[DMDIAG] ===================== DEATHMATCH SELF-CHECK =====================");

            try
            {
                Avatar avatar = CurrentAvatar;
                Region region = avatar?.Region;

                // ---------- match state ----------
                Line(sb, _deathmatchActive, "match active", _deathmatchActive);
                Line(sb, _tdmActive, "teams mode active", _tdmActive);
                Line(sb, _tdmTeamSize > 0, "team size", $"{_tdmTeamSize}v{_tdmTeamSize}v{_tdmTeamSize}");
                Line(sb, _tdmKillTarget > 0, "kill target", _tdmKillTarget);

                // ---------- region ----------
                if (region == null)
                {
                    Line(sb, false, "region", "NULL", "everything below is meaningless");
                }
                else
                {
                    var rp = region.Prototype;
                    Line(sb, true, "region", region.PrototypeName);
                    Line(sb, true, "region ref", $"0x{(ulong)region.PrototypeDataRef:X}");
                    bool matchPlay = rp != null && rp.Behavior == RegionBehavior.MatchPlay;
                    Line(sb, matchPlay, "region.Behavior", rp?.Behavior,
                        matchPlay ? "MatchPlay, same as real PvP regions" : "real PvP regions are MatchPlay(4) — scoreboard suspect");
                    Line(sb, true, "region.PlayerLimit", rp?.PlayerLimit);
                    Line(sb, true, "region.MetaGames count", region.MetaGames.Count);

                    // Enumerate every metagame still attached to the region. Both
                    // remaining faults point here: Midtown Patrol owns the "Next
                    // Event" bar AND the shared CurrentCountTotalCountOnly widget
                    // the score is written to, so if it survives sterilization it
                    // overwrites the score with its own counter. This tells apart
                    // "still alive" from "dead but its UI persists".
                    foreach (ulong mgId in region.MetaGames)
                    {
                        var mg = Game.EntityManager.GetEntity<MetaGame>(mgId);
                        bool mine = mgId == _deathmatchMetaGameId;
                        if (mg == null)
                        {
                            Line(sb, true, $"  metagame {mgId}", "not resolvable (already destroyed)");
                            continue;
                        }

                        string modeName = mg.CurrentMode?.PrototypeDataRef.GetNameFormatted() ?? "<no active mode>";
                        Line(sb, mine, $"  metagame {mgId}",
                            $"{mg.PrototypeDataRef.GetNameFormatted()} destroyed={mg.IsDestroyed} mode={modeName}",
                            mine ? "ours" : "FOREIGN — owns its own UI/timer and will fight us for widgets");
                    }
                }

                // ---------- metagame ----------
                var pvp = GetDeathmatchMetaGame();
                if (pvp == null)
                {
                    Line(sb, false, "metagame", "NULL", "no scoreboard is possible without one");
                }
                else
                {
                    Line(sb, true, "metagame id", pvp.Id);
                    Line(sb, true, "metagame proto", pvp.PrototypeDataRef.GetNameFormatted());
                    Line(sb, pvp.GetRegion() == region, "metagame region matches", pvp.GetRegion() == region);
                    Line(sb, pvp.PvPScore != null, "ScoreTable", pvp.PvPScore != null ? "present" : "NULL",
                        pvp.PvPScore == null ? "ScoreSchemaPlayer patch did not apply" : null);
                    Line(sb, pvp.EventHandler != null, "EventHandler", pvp.EventHandler?.GetType().Name ?? "NULL",
                        pvp.EventHandler == null ? "no automatic kill/death scoring" : null);

                    var proto = pvp.PvPPrototype;
                    Line(sb, proto != null && proto.IsPvP, "proto.IsPvP", proto?.IsPvP);
                    Line(sb, proto?.Teams != null && proto.Teams.Length == 3, "proto.Teams", proto?.Teams?.Length ?? 0);
                    Line(sb, proto != null && proto.ScoreSchemaPlayer != PrototypeId.Invalid,
                        "proto.ScoreSchemaPlayer", proto?.ScoreSchemaPlayer);
                    Line(sb, proto?.GameModes != null && proto.GameModes.Length > 0,
                        "proto.GameModes", proto?.GameModes?.Length ?? 0,
                        (proto?.GameModes?.Length ?? 0) == 0 ? "no mode => OnActivate never runs => no scoreboard message" : null);
                    Line(sb, proto != null && proto.MiniMapFilter != PrototypeId.Invalid, "proto.MiniMapFilter", proto?.MiniMapFilter);

                    // ---------- the active mode: this is what emits the scoreboard ----------
                    var mode = pvp.CurrentMode;
                    if (mode == null)
                    {
                        Line(sb, false, "CurrentMode", "NULL", "mode never activated — ActivateGameMode(0) did not run");
                    }
                    else
                    {
                        var mp = mode.Prototype;
                        Line(sb, true, "CurrentMode", mode.GetType().Name);
                        Line(sb, true, "mode proto", mode.PrototypeDataRef.GetNameFormatted());
                        Line(sb, mp != null && mp.ShowScoreboard, "mode.ShowScoreboard", mp?.ShowScoreboard,
                            (mp != null && mp.ShowScoreboard) ? "NetMessageShowPvPScoreboard WAS sent on activate"
                                                              : "patch did not apply — message never sent");
                        Line(sb, mp != null && mp.EventHandler != PrototypeId.Invalid, "mode.EventHandler", mp?.EventHandler);
                        Line(sb, mp != null && mp.AvatarOnKilledInfoOverride != PrototypeId.Invalid,
                            "mode.AvatarOnKilledInfo", mp?.AvatarOnKilledInfoOverride, "drives the respawn dialog");
                        if (mp is MetaGameModeIdlePrototype idle)
                        {
                            Line(sb, idle.DurationMS == 0, "mode.DurationMS", idle.DurationMS,
                                idle.DurationMS == 0 ? "terminal, will not advance" : "WILL advance to another mode");
                            Line(sb, true, "mode.NextMode", idle.NextMode);
                            Line(sb, idle.PlayersCanMove, "mode.PlayersCanMove", idle.PlayersCanMove);
                        }
                    }

                    // ---------- teams ----------
                    for (int t = 0; t < pvp.Teams.Count; t++)
                    {
                        var team = pvp.Teams[t] as PvPTeam;
                        Line(sb, team?.Alliance != null, $"team[{t}]",
                            $"{pvp.Teams[t].ProtoRef.GetNameFormatted()} alliance={team?.Alliance?.DataRef.GetNameFormatted() ?? "NONE"} players={pvp.Teams[t].TeamSize}");
                    }
                }

                // ---------- the player ----------
                if (avatar != null)
                {
                    string myAlliance = avatar.Alliance?.DataRef.GetNameFormatted() ?? "NULL";
                    bool onPvpTeam = myAlliance.Contains("PVPTeam");
                    Line(sb, onPvpTeam, "player alliance", myAlliance,
                        onPvpTeam ? null : "player is NOT on a PvP alliance — nothing will be hostile");
                    Line(sb, _tdmMyTeam >= 0, "player team index", _tdmMyTeam);
                    Line(sb, true, "player position", avatar.RegionLocation.Position.ToStringNames());
                }

                // ---------- anchors ----------
                for (int i = 0; i < DeathmatchTeamCount; i++)
                    Line(sb, true, $"anchor[{i}]", _tdmTeamAnchors[i].ToStringNames());

                bool anchorsOk = true;
                for (int a = 0; a < DeathmatchTeamCount; a++)
                    for (int b = a + 1; b < DeathmatchTeamCount; b++)
                    {
                        float sep = Vector3.Distance2D(_tdmTeamAnchors[a], _tdmTeamAnchors[b]);
                        bool ok = sep >= 1000f;
                        anchorsOk &= ok;
                        Line(sb, ok, $"anchor sep {a}<->{b}", $"{sep:F0}u", ok ? null : "teams start on top of each other");
                    }

                // ---------- combatants ----------
                Line(sb, _tdmCombatantTeam.Count > 0, "combatants tracked", _tdmCombatantTeam.Count);

                var perTeam = new int[DeathmatchTeamCount];
                int missing = 0, wrongAlliance = 0, farFromAnchor = 0;
                foreach (var kvp in _tdmCombatantTeam)
                {
                    if (kvp.Value >= 0 && kvp.Value < DeathmatchTeamCount) perTeam[kvp.Value]++;
                    if (kvp.Key == CurrentAvatar?.Id) continue;

                    var agent = Game.EntityManager.GetEntity<Agent>(kvp.Key);
                    if (agent == null || agent.IsInWorld == false) { missing++; continue; }

                    string ally = agent.Alliance?.DataRef.GetNameFormatted() ?? "NULL";
                    var expected = (pvp?.Teams.Count > kvp.Value ? pvp.Teams[kvp.Value] as PvPTeam : null)?.Alliance?.DataRef.GetNameFormatted();
                    if (expected != null && ally != expected) wrongAlliance++;

                    if (Vector3.Distance2D(agent.RegionLocation.Position, _tdmTeamAnchors[kvp.Value]) > 3000f) farFromAnchor++;
                }

                for (int t = 0; t < DeathmatchTeamCount; t++)
                    Line(sb, perTeam[t] > 0 && perTeam[t] <= _tdmTeamSize, $"team[{t}] size", $"{perTeam[t]}/{_tdmTeamSize}",
                        perTeam[t] > _tdmTeamSize ? "OVER the per-team cap" : (perTeam[t] == 0 ? "empty" : null));

                Line(sb, missing == 0, "combatants missing", missing, missing > 0 ? "tracked but not in world" : null);
                Line(sb, wrongAlliance == 0, "wrong alliance", wrongAlliance, wrongAlliance > 0 ? "team/alliance mismatch" : null);
                Line(sb, true, "far from anchor", farFromAnchor, "expected once they start roaming");

                // ---------- the score widget ----------
                // The score is written into a UIWidgetGenericFraction. That widget
                // prototype is SHARED (Endless Challenge and Trial use the same
                // one), so a region metagame writing to it will silently clobber
                // the deathmatch score. Read the value back to see who won.
                try
                {
                    PrototypeId widgetRef = GetEndlessWaveWidgetRef();
                    Line(sb, widgetRef != PrototypeId.Invalid, "score widget ref",
                        widgetRef == PrototypeId.Invalid ? "NONE RESOLVED" : widgetRef.GetNameFormatted());

                    if (widgetRef != PrototypeId.Invalid && region != null)
                    {
                        var widget = region.UIDataProvider?.GetWidget<UIWidgetGenericFraction>(widgetRef);
                        if (widget == null)
                        {
                            Line(sb, false, "score widget", "NULL", "UIDataProvider has no widget instance");
                        }
                        else
                        {
                            int expected = _tdmMyTeam >= 0 ? _tdmTeamScores[_tdmMyTeam] : 0;
                            Line(sb, true, "score widget expects", $"{expected}/{_tdmKillTarget}");
                            // Push our value, then read it straight back — if it
                            // does not stick, something else owns this widget.
                            widget.SetCount(expected, _tdmKillTarget);
                            Line(sb, true, "score widget after write", "written (compare against the bar on screen)");
                        }
                    }
                }
                catch (Exception ex) { Line(sb, false, "score widget", "threw", ex.Message); }

                // ---------- arena cleanliness ----------
                if (region != null)
                {
                    int foreignAgents = 0, spawners = 0;
                    try
                    {
                        var sphere = new MHServerEmu.Core.Collisions.Sphere(region.Aabb.Center,
                            MathF.Max(region.Aabb.Width, region.Aabb.Length));
                        var ctx = new EntityRegionSPContext(EntityRegionSPContextFlags.PrimaryPartition);
                        foreach (WorldEntity we in region.IterateEntitiesInVolume(sphere, ctx))
                        {
                            if (we == null || we.IsInWorld == false) continue;
                            if (we is Spawner) { spawners++; continue; }
                            if (we is not Agent) continue;
                            if (we is Avatar) continue;                       // players + phantoms
                            if (_tdmCombatantTeam.ContainsKey(we.Id)) continue;
                            if (we.Id == _deathmatchNpcId) continue;
                            foreignAgents++;
                        }
                    }
                    catch (Exception ex) { Line(sb, false, "arena sweep", "threw", ex.Message); }

                    Line(sb, foreignAgents == 0, "non-combatant agents", foreignAgents,
                        foreignAgents > 0 ? "arena not sterile — region population still alive" : null);
                    Line(sb, spawners == 0, "live spawners", spawners,
                        spawners > 0 ? "will keep producing mobs" : null);
                    Line(sb, _tdmSterilizePassesLeft == 0, "sterilize passes left", _tdmSterilizePassesLeft);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[DMDIAG] BAD self-check threw: {ex}");
            }

            sb.AppendLine("[DMDIAG] ================================================================");
            DeathmatchLogger.Info(sb.ToString());
        }

        private sealed class DeathmatchDiagEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.DumpDeathmatchDiagnostics();
        }
    }
}
