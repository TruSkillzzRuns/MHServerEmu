using MHServerEmu.Core.Memory;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Missions.Conditions
{
    public class MissionConditionAvatarIsActive : MissionPlayerCondition
    {
        private MissionConditionAvatarIsActivePrototype _proto;
        private Event<PlayerSwitchedToAvatarGameEvent>.Action _playerSwitchedToAvatarAction;

        public MissionConditionAvatarIsActive(Mission mission, IMissionConditionOwner owner, MissionConditionPrototype prototype) 
            : base(mission, owner, prototype)
        {
            // TimesBehaviorController
            _proto = prototype as MissionConditionAvatarIsActivePrototype;
            _playerSwitchedToAvatarAction = OnPlayerSwitchedToAvatar;
        }

        public override bool OnReset()
        {
            SetCompletion(IsAvatarActiveForAnyParticipant());
            return true;
        }

        /// <summary>
        /// Whether any mission participant currently has the tracked avatar active.
        /// </summary>
        private bool IsAvatarActiveForAnyParticipant()
        {
            using var participantsHandle = ListPool<Player>.Instance.Get(out List<Player> participants);
            if (Mission.GetParticipants(participants) == false)
                return false;

            foreach (var player in participants)
            {
                var avatar = player.CurrentAvatar;
                if (avatar != null && avatar.PrototypeDataRef == _proto.AvatarPrototype)
                    return true;
            }

            return false;
        }

        private void OnPlayerSwitchedToAvatar(in PlayerSwitchedToAvatarGameEvent evt)
        {
            var player = evt.Player;
            var avatarRef = evt.AvatarRef;

            if (player == null || IsMissionPlayer(player) == false) return;

            if (_proto.AvatarPrototype == avatarRef)
            {
                UpdatePlayerContribution(player);
                SetCompleted();
                return;
            }

            // Switching AWAY from the tracked avatar. This previously just returned, so the
            // condition stayed completed for a hero the player was no longer playing and the
            // client's mission tracker kept showing it -- OnReset() was the only thing that
            // ever cleared it, and that only runs on mission/objective state transitions.
            // Re-evaluate across all participants rather than clearing outright, so a party
            // member swapping heroes doesn't invalidate the condition while someone else
            // still has the tracked avatar active. Player.SwitchAvatar() fires this event
            // after EnableCurrentAvatar(), so CurrentAvatar already reflects the new hero.
            SetCompletion(IsAvatarActiveForAnyParticipant());
        }

        public override void RegisterEvents(Region region)
        {
            EventsRegistered = true;
            region.PlayerSwitchedToAvatarEvent.AddActionBack(_playerSwitchedToAvatarAction);
        }

        public override void UnRegisterEvents(Region region)
        {
            EventsRegistered = false;
            region.PlayerSwitchedToAvatarEvent.RemoveAction(_playerSwitchedToAvatarAction);
        }
    }
}
