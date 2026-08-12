using System;
using System.Collections.Generic;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Powers
{
    // OmegaDev2 DPS meter — server-side damage accounting per avatar
    // (the human's hero and every phantom individually). Fed from
    // WorldEntity.ApplyHealthPowerResults with the ACTUAL applied health
    // delta, so resists, clamps and invulnerability are already factored in.
    //
    // Writes happen on the game thread; reads come from WebFrontend threads —
    // everything is guarded by one lock and each operation is O(small).
    public static class DpsMeter
    {
        private const long WindowLongMs = 60_000;
        private const long WindowShortMs = 10_000;
        // A single power activation resolves as MULTIPLE separate RecordDamage
        // calls (once per target hit by an AOE, once per beam-sweep tick, once
        // per bounce) — there's no activation/cast id to correlate them by, so
        // this groups calls that land within a short window of each other into
        // one "burst" for peak-hit purposes. Without this, PeakHit would only
        // ever show the single largest per-target/per-tick share, not what a
        // player perceives as "that cast hit for X."
        private const long PeakBurstWindowMs = 150;

        private sealed class Entry
        {
            public string Name;
            public bool IsPhantom;
            public ulong OwnerPlayerId;     // human player entity id (phantom damage resolves to its creator)
            public long Total;
            public long PeakHit;
            public long CurrentBurstSum;
            public long CurrentBurstMs;
            public long FirstMs;
            public long LastMs;
            public readonly Queue<(long Ms, long Amount)> Recent = new();

            // Per-power breakdown, keyed by power prototype. Scoped INSIDE the
            // entry so a hero and their phantoms never blur together — the
            // meter already tracks each avatar separately and this must follow.
            public readonly Dictionary<PrototypeId, PowerEntry> Powers = new();
        }

        private sealed class PowerEntry
        {
            public string Name;
            public long Total;
            public long Hits;
            public long PeakHit;
            public long CurrentBurstSum;
            public long CurrentBurstMs;
            public long LastMs;
            public readonly Queue<(long Ms, long Amount)> Recent = new();
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<ulong, Entry> _entries = new();   // key: avatar entity id
        private static long _resetMs = Environment.TickCount64;

        /// <summary>
        /// Record damage dealt by an avatar (game thread).
        /// <paramref name="powerProto"/> may be null — damage-over-time ticks
        /// and environmental sources arrive without one, and those are folded
        /// into an "(unattributed)" row rather than dropped, so the per-power
        /// totals still add up to the combatant total.
        /// </summary>
        public static void RecordDamage(Avatar attacker, long amount, PowerPrototype powerProto = null)
        {
            if (amount <= 0 || attacker == null) return;

            long now = Environment.TickCount64;

            lock (_lock)
            {
                if (_entries.TryGetValue(attacker.Id, out Entry entry) == false)
                {
                    Player rawOwner = attacker.GetOwnerOfType<Player>();
                    Player creditOwner = Player.ResolveCreditPlayer(rawOwner);

                    string heroName = LeafOf(attacker.PrototypeDataRef.GetName());
                    string ownerName = rawOwner?.GetName() ?? "?";

                    entry = new Entry
                    {
                        Name = $"{heroName} ({ownerName})",
                        IsPhantom = attacker.IsPhantomHero,
                        OwnerPlayerId = creditOwner?.Id ?? 0,
                        FirstMs = now,
                    };
                    _entries[attacker.Id] = entry;
                }

                entry.Total += amount;

                if (now - entry.CurrentBurstMs <= PeakBurstWindowMs)
                    entry.CurrentBurstSum += amount;
                else
                    entry.CurrentBurstSum = amount;
                entry.CurrentBurstMs = now;
                if (entry.CurrentBurstSum > entry.PeakHit) entry.PeakHit = entry.CurrentBurstSum;

                entry.LastMs = now;
                entry.Recent.Enqueue((now, amount));
                TrimRecent(entry, now);

                RecordPowerDamage(entry, powerProto, amount, now);
            }
        }

        /// <summary>Attribute one damage event to a power. Caller holds the lock.</summary>
        private static void RecordPowerDamage(Entry entry, PowerPrototype powerProto, long amount, long now)
        {
            PrototypeId key = powerProto?.DataRef ?? PrototypeId.Invalid;

            if (entry.Powers.TryGetValue(key, out PowerEntry power) == false)
            {
                power = new PowerEntry
                {
                    Name = key != PrototypeId.Invalid ? LeafOf(key.GetName()) : "(unattributed)",
                };
                entry.Powers[key] = power;
            }

            power.Total += amount;
            power.Hits++;

            // Same burst grouping as the combatant total: one cast can resolve
            // as many RecordDamage calls (AOE targets, beam ticks, bounces), so
            // without this a peak reads as a single target's share.
            if (now - power.CurrentBurstMs <= PeakBurstWindowMs)
                power.CurrentBurstSum += amount;
            else
                power.CurrentBurstSum = amount;
            power.CurrentBurstMs = now;
            if (power.CurrentBurstSum > power.PeakHit) power.PeakHit = power.CurrentBurstSum;

            power.LastMs = now;
            power.Recent.Enqueue((now, amount));

            while (power.Recent.Count > 0 && now - power.Recent.Peek().Ms > WindowLongMs)
                power.Recent.Dequeue();
        }

        public static void Reset()
        {
            lock (_lock)
            {
                _entries.Clear();
                _resetMs = Environment.TickCount64;
            }
        }

        public sealed class Snapshot
        {
            public string Name { get; set; }
            public bool IsPhantom { get; set; }
            public long Total { get; set; }
            public long PeakHit { get; set; }
            public double Dps10 { get; set; }
            public double Dps60 { get; set; }
            public double DpsOverall { get; set; }
            public long SecondsSinceLastHit { get; set; }

            /// <summary>Per-power breakdown for this combatant, biggest first.</summary>
            public List<PowerSnapshot> Powers { get; set; } = new();
        }

        public sealed class PowerSnapshot
        {
            public string Name { get; set; }
            public long Total { get; set; }
            public long Hits { get; set; }
            public long PeakHit { get; set; }
            public double AvgHit { get; set; }
            /// <summary>Share of this combatant's total damage, 0-100.</summary>
            public double PercentOfTotal { get; set; }
            public double Dps60 { get; set; }
            public long SecondsSinceLastHit { get; set; }
        }

        /// <summary>
        /// Snapshot all combatants belonging to <paramref name="ownerPlayerId"/>
        /// (0 = everyone tracked). Safe to call from any thread.
        /// </summary>
        public static List<Snapshot> GetSnapshots(ulong ownerPlayerId)
        {
            long now = Environment.TickCount64;
            var list = new List<Snapshot>();

            lock (_lock)
            {
                foreach (Entry entry in _entries.Values)
                {
                    if (ownerPlayerId != 0 && entry.OwnerPlayerId != ownerPlayerId) continue;

                    TrimRecent(entry, now);

                    long sum10 = 0, sum60 = 0;
                    foreach (var (ms, amount) in entry.Recent)
                    {
                        sum60 += amount;
                        if (now - ms <= WindowShortMs) sum10 += amount;
                    }

                    // Overall = total damage over active time (first hit -> last
                    // hit, min 1s) so a parked meter doesn't decay to zero.
                    double activeSeconds = Math.Max(1.0, (entry.LastMs - entry.FirstMs) / 1000.0);

                    list.Add(new Snapshot
                    {
                        Name = entry.Name,
                        IsPhantom = entry.IsPhantom,
                        Total = entry.Total,
                        PeakHit = entry.PeakHit,
                        Dps10 = sum10 / (WindowShortMs / 1000.0),
                        Dps60 = sum60 / (WindowLongMs / 1000.0),
                        DpsOverall = entry.Total / activeSeconds,
                        SecondsSinceLastHit = (now - entry.LastMs) / 1000,
                        Powers = BuildPowerSnapshots(entry, now),
                    });
                }
            }

            list.Sort((a, b) => b.Total.CompareTo(a.Total));
            return list;
        }

        /// <summary>Caller holds the lock.</summary>
        private static List<PowerSnapshot> BuildPowerSnapshots(Entry entry, long now)
        {
            var powers = new List<PowerSnapshot>(entry.Powers.Count);

            foreach (PowerEntry power in entry.Powers.Values)
            {
                while (power.Recent.Count > 0 && now - power.Recent.Peek().Ms > WindowLongMs)
                    power.Recent.Dequeue();

                long sum60 = 0;
                foreach (var (_, amount) in power.Recent) sum60 += amount;

                powers.Add(new PowerSnapshot
                {
                    Name = power.Name,
                    Total = power.Total,
                    Hits = power.Hits,
                    PeakHit = power.PeakHit,
                    AvgHit = power.Hits > 0 ? (double)power.Total / power.Hits : 0,
                    PercentOfTotal = entry.Total > 0 ? power.Total * 100.0 / entry.Total : 0,
                    Dps60 = sum60 / (WindowLongMs / 1000.0),
                    SecondsSinceLastHit = (now - power.LastMs) / 1000,
                });
            }

            powers.Sort((a, b) => b.Total.CompareTo(a.Total));
            return powers;
        }

        public static long SecondsSinceReset => (Environment.TickCount64 - _resetMs) / 1000;

        private static void TrimRecent(Entry entry, long now)
        {
            while (entry.Recent.Count > 0 && now - entry.Recent.Peek().Ms > WindowLongMs)
                entry.Recent.Dequeue();
        }

        private static string LeafOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }
    }
}
