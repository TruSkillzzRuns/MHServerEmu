using System.Collections.Generic;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.Games.Entities.Avatars
{
    /// <summary>
    /// Shared parsing/formatting helpers for phantom-hero commands and web
    /// endpoints, so the level/count clamps and "multiple matches" messaging
    /// only live in one place instead of being copy-pasted per call site.
    /// </summary>
    public static class PhantomCommandUtil
    {
        public const int MinLevel = 1;
        public const int MaxLevel = 60;

        /// <summary>
        /// Parses an optional level argument at <paramref name="idx"/>, clamped
        /// to [1, 60]. Returns 0 (meaning "match caller's level") if the arg is
        /// absent or not a valid integer.
        /// </summary>
        public static int ParseLevelClamp(string[] @params, int idx)
        {
            if (@params.Length > idx && int.TryParse(@params[idx], out int l))
                return System.Math.Clamp(l, MinLevel, MaxLevel);
            return 0;
        }

        /// <summary>
        /// Parses an optional rank argument at <paramref name="idx"/>, clamped
        /// to [1, 5]. Returns <paramref name="defaultValue"/> if absent/invalid.
        /// </summary>
        public static int ParseRankClamp(string[] @params, int idx, int defaultValue)
        {
            if (@params.Length > idx && int.TryParse(@params[idx], out int r))
                return System.Math.Clamp(r, 1, 5);
            return defaultValue;
        }

        /// <summary>
        /// Clamps a requested spawn count to [1, max]. <paramref name="raw"/>
        /// should be the parsed integer; <paramref name="defaultValue"/> is
        /// used when no count arg was given at all.
        /// </summary>
        public static int ClampCount(int raw, int defaultValue, int max)
            => raw > 0 ? System.Math.Clamp(raw, 1, max) : defaultValue;

        /// <summary>
        /// Formats a "Multiple matches: A, B, C, ..." message for an ambiguous
        /// hero/team-up name lookup, capping the listed names so a broad query
        /// doesn't dump the entire roster into chat.
        /// </summary>
        public static string FormatMultipleMatches(IReadOnlyList<(PrototypeId Ref, string ShortName)> matches, int cap = 8)
        {
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < matches.Count && i < cap; i++)
            {
                if (i > 0) names.Append(", ");
                names.Append(matches[i].ShortName);
            }
            if (matches.Count > cap) names.Append(", ...");
            return $"Multiple matches: {names}. Be more specific.";
        }
    }
}
