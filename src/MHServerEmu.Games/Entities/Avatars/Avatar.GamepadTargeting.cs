using System;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities.Avatars
{
    // Gamepad target-lock compensation. Reported symptom: on a gamepad, target
    // lock only works on NPCs (locking another real player is a deliberate
    // client-side anti-grief restriction — you shouldn't be able to
    // accidentally lock a nearby player while aiming at a mob). When no NPC
    // is nearby to lock, target-centered powers activate with TargetEntityId
    // invalid, and the only server-side fallback for that case
    // (Avatar.FixupPendingActivateSettings) shoves the power out to max
    // range instead of landing on anything. This fills the gap: if a
    // legitimate nearby hostile exists, use it instead of leaving the power
    // target-less.
    public partial class Avatar
    {
        /// <summary>
        /// Find the nearest hostile, non-player entity within the gamepad
        /// auto-target-lock radius and roughly in the direction the player
        /// was aiming. Returns 0 (InvalidId) if nothing qualifies — callers
        /// must treat that as "leave existing behavior alone", not an error.
        /// </summary>
        private ulong FindNearestHostileForGamepadTarget(Vector3 aimTargetPosition)
        {
#if GAME_VERSION_1_52 || GAME_VERSION_1_53
            Region region = Region;
            if (region == null) return Entity.InvalidId;

            var gamepadGlobals = GameDatabase.GamepadGlobalsPrototype;
            if (gamepadGlobals == null) return Entity.InvalidId;

            float radius = gamepadGlobals.GamepadAutoTargetLockRadius;
            if (radius <= 0f) return Entity.InvalidId;

            Vector3 origin = RegionLocation.Position;
            Vector3 aimDirection = Vector3.SafeNormalize(aimTargetPosition - origin);
            // Half-angle cone around the aim direction so the substitution
            // stays roughly where the player was pointing rather than
            // grabbing something behind them. Data is in degrees.
            float halfAngleRad = MathHelper.ToRadians(gamepadGlobals.GamepadTargetLockAssistHalfAngle);
            float cosHalfAngle = MathF.Cos(halfAngleRad);
            bool haveAimDirection = Vector3.LengthSquared(aimDirection) > 0.0001f;

            var sphere = new MHServerEmu.Core.Collisions.Sphere(origin, radius);
            var ctx = new EntityRegionSPContext(EntityRegionSPContextFlags.PrimaryPartition);

            ulong bestId = Entity.InvalidId;
            float bestDistSq = float.MaxValue;

            foreach (WorldEntity we in region.IterateEntitiesInVolume(sphere, ctx))
            {
                if (we == null || we.Id == Id) continue;
                if (we is Avatar) continue; // never auto-target a real player
                if (we.IsInWorld == false || we.IsDead) continue;
                if (we.IsDormant || we.IsUntargetable || we.IsUnaffectable) continue;
                if (IsHostileTo(we) == false) continue;

                Vector3 toCandidate = we.RegionLocation.Position - origin;
                float distSq = Vector3.LengthSquared(toCandidate);
                if (distSq >= bestDistSq) continue;

                if (haveAimDirection)
                {
                    Vector3 candidateDir = Vector3.SafeNormalize(toCandidate);
                    float cosAngle = Vector3.Dot(aimDirection, candidateDir);
                    if (cosAngle < cosHalfAngle) continue;
                }

                bestDistSq = distSq;
                bestId = we.Id;
            }

            return bestId;
#else
            // GamepadGlobalsPrototype doesn't exist under 1.48 -- this
            // feature simply has nothing to hook into on that version, so
            // fall back to "no substitution" (same as region == null above).
            return Entity.InvalidId;
#endif
        }
    }
}
