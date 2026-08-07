using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.GameData.Tables
{
    public class AllianceTable
    {
        private readonly bool[][] _friendlyLookup;
        private readonly bool[][] _hostileLookup;

        public AllianceTable()
        {
            DataDirectory dataDirectory = GameDatabase.DataDirectory;

            // Get the alliance blueprint and figure out the total number of alliances
            GlobalsPrototype globals = GameDatabase.GlobalsPrototype;
            BlueprintId allianceBlueprintRef = dataDirectory.GetPrototypeBlueprintDataRef(globals.AnyAlliancePrototype.DataRef);
            int numAlliances = dataDirectory.GetPrototypeMaxEnumValue(allianceBlueprintRef) + 1;

            // Allocate our table for every alliance combination using jagged arrays, the default value for both friendliness and hostility is false.
            // NOTE: The client uses nested vectors here, which we don't have to do, since we don't use any hotloading.
            _friendlyLookup = new bool[numAlliances][];
            _hostileLookup = new bool[numAlliances][];

            for (int i = 0; i < numAlliances; i++)
            {
                _friendlyLookup[i] = new bool[numAlliances];
                _hostileLookup[i] = new bool[numAlliances];
            }


            // Fill our table with data from alliance prototypes
            foreach (PrototypeId alliancePrototypeRef in dataDirectory.IteratePrototypesInHierarchy<AlliancePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                AlliancePrototype alliancePrototype = alliancePrototypeRef.As<AlliancePrototype>();
                if (!Verify.IsNotNull(alliancePrototype))
                    continue;

                // Alliances are automatically friendly to themselves
                _friendlyLookup[alliancePrototype.EnumValue][alliancePrototype.EnumValue] = true;

                // Add all friendliness flags from the prototype
                if (alliancePrototype.FriendlyTo != null)
                {
                    foreach (PrototypeId friendlyAllianceRef in alliancePrototype.FriendlyTo)
                    {
                        int friendlyEnumValue = dataDirectory.GetPrototypeEnumValue(friendlyAllianceRef, allianceBlueprintRef);
                        _friendlyLookup[alliancePrototype.EnumValue][friendlyEnumValue] = true;
                    }
                }

                // Add all hostility flags from the prototype
                if (alliancePrototype.HostileTo != null)
                {
                    foreach (PrototypeId hostileAllianceRef in alliancePrototype.HostileTo)
                    {
                        int hostileEnumValue = dataDirectory.GetPrototypeEnumValue(hostileAllianceRef, allianceBlueprintRef);
                        _hostileLookup[alliancePrototype.EnumValue][hostileEnumValue] = true;
                    }
                }
            }
        }

        /// <summary>
        /// TEMPORARY DIAGNOSTIC (Midtown Deathmatch investigation).
        ///
        /// Forces a hostility bit on server-side only. The CLIENT builds this
        /// same table from the same prototype data, so after calling this the
        /// two sides deliberately DISAGREE. That is the whole point: it isolates
        /// whether the client independently blocks attacks on an entity its own
        /// data says is friendly, or whether it defers to the server.
        ///
        /// The answer decides whether an 8-way free-for-all is possible at all:
        /// stock data has only three mutually hostile PvP alliances
        /// (PVPTeam1RED / 2WHITE / 3BLUE) and no alliance is hostile to itself,
        /// so FFA beyond 3 players needs the client to defer to the server.
        ///
        /// Remove this once the question is answered.
        /// </summary>
        public bool ForceHostileForTesting(AlliancePrototype lhs, AlliancePrototype rhs, bool hostile = true)
        {
            if (lhs == null || rhs == null) return false;
            if (lhs.EnumValue >= _hostileLookup.Length || rhs.EnumValue >= _hostileLookup.Length) return false;

            _hostileLookup[lhs.EnumValue][rhs.EnumValue] = hostile;
            _hostileLookup[rhs.EnumValue][lhs.EnumValue] = hostile;
            return true;
        }

        public bool IsFriendlyTo(AlliancePrototype lhsAllianceProto, AlliancePrototype rhsAllianceProto)
        {
            if (!Verify.IsTrue(lhsAllianceProto.EnumValue < _friendlyLookup.Length && rhsAllianceProto.EnumValue < _friendlyLookup.Length)) return false;
            return _friendlyLookup[lhsAllianceProto.EnumValue][rhsAllianceProto.EnumValue];
        }

        public bool IsHostileTo(AlliancePrototype lhsAllianceProto, AlliancePrototype rhsAllianceProto)
        {
            if (!Verify.IsTrue(lhsAllianceProto.EnumValue < _hostileLookup.Length && rhsAllianceProto.EnumValue < _hostileLookup.Length)) return false;
            return _hostileLookup[lhsAllianceProto.EnumValue][rhsAllianceProto.EnumValue];
        }
    }
}
