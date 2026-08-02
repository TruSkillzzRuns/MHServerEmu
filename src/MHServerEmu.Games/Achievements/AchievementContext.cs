using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Achievements
{
    public enum EventContextType
    {
        Avatar,
        Item,
        Party,
        Pet,
        Region,
        DifficultyTierMin,
        DifficultyTierMax,
        TeamUp,
        PublicEventTeam
    }

    public class AchievementContext
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        public uint Id { get; set; }
        public long RewardPrototype { get; set; }
        public List<AchievementEventData> EventData { get; set; }
        public List<AchievementEventContext> EventContext { get; set; }

        /// <summary>
        /// Number of achievement prototype references that failed to resolve since the last
        /// <see cref="ResetUnresolvedCount"/>. Achievement data is shared across game versions,
        /// so older clients legitimately lack prototypes referenced by newer achievements --
        /// these are counted and reported once by AchievementDatabase.Initialize() rather than
        /// logged individually, which produced hundreds of warnings per startup on 1.48.
        /// </summary>
        public static int UnresolvedCount { get; private set; }

        public static void ResetUnresolvedCount()
        {
            UnresolvedCount = 0;
        }

        /// <summary>
        /// Set by <see cref="GetPrototype"/> when a non-zero prototype guid fails to resolve on
        /// this game version. Callers building an achievement's filters check this to tell an
        /// intentionally-absent filter (guid 0, "match anything") apart from one that is simply
        /// missing from this client's data.
        /// </summary>
        public static bool LastResolveFailed { get; private set; }

        public static void ClearLastResolveFailed()
        {
            LastResolveFailed = false;
        }

        public static Prototype GetPrototype(long prototypeGuid)
        {
            if (prototypeGuid == 0) return null;
            PrototypeId protoRef = GameDatabase.GetDataRefByPrototypeGuid((PrototypeGuid)prototypeGuid);
            if (protoRef == PrototypeId.Invalid)
            {
                UnresolvedCount++;
                LastResolveFailed = true;
                Logger.Trace($"GetPrototype Guid {prototypeGuid} have not DataRef");
                return null;
            }
            Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
            if (proto == null)
            {
                UnresolvedCount++;
                LastResolveFailed = true;
                Logger.Trace($"GetPrototype DataRef {protoRef} have not Prototype");
                return null;
            }
            return proto;
        }

        public ScoringEventData GetScoringEventData()
        {
            ScoringEventData eventData = new();
            int count = EventData.Count;

            if (count == 0) return eventData;
            eventData.Proto0 = GetPrototype(EventData[0].Prototype);
            eventData.Proto0IncludeChildren = EventData[0].IncludeChildren;

            if (count == 1) return eventData;            
            eventData.Proto1 = GetPrototype(EventData[1].Prototype);
            eventData.Proto1IncludeChildren = EventData[1].IncludeChildren;            

            if (count == 2) return eventData;            
            eventData.Proto2 = GetPrototype(EventData[2].Prototype);
            eventData.Proto2IncludeChildren = EventData[2].IncludeChildren;
            
            return eventData;
        }

        public ScoringEventContext GetScoringEventContext()
        {
            ScoringEventContext eventContext = new();
            foreach (var context in EventContext)
            {
                var prototype = GetPrototype(context.Prototype);
                switch (context.ContextType) {
                    case EventContextType.Region:
                        eventContext.Region = prototype;
                        eventContext.RegionIncludeChildren = context.IncludeChildren;
                        break;
                    case EventContextType.Avatar:
                        eventContext.Avatar = prototype;
                        break;
                    case EventContextType.Item:
                        eventContext.Item = prototype;
                        break;
                    case EventContextType.Party:
                        eventContext.Party = prototype;
                        break;
                    case EventContextType.Pet:
                        eventContext.Pet = prototype;
                        break;
#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                    case EventContextType.DifficultyTierMin:
                        eventContext.DifficultyTierMin = prototype as DifficultyTierPrototype;
                        break;
                    case EventContextType.DifficultyTierMax:
                        eventContext.DifficultyTierMax = prototype as DifficultyTierPrototype;
                        break;
#endif
                    case EventContextType.TeamUp:
                        eventContext.TeamUp = prototype;
                        break;
                    case EventContextType.PublicEventTeam:
                        eventContext.PublicEventTeam = prototype;
                        break;
                }
            }
            return eventContext;
        }
    }

    public struct AchievementEventData
    {
        public long Prototype { get; set; }
        public bool IncludeChildren { get; set; }
    }

    public struct AchievementEventContext
    {
        public EventContextType ContextType { get; set; }
        public long Prototype { get; set; }
        public bool IncludeChildren { get; set; }
    }
}
