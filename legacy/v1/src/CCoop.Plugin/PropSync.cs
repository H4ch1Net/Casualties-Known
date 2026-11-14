using System;
using CCoop.Protocol.Messages;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// Shares the state of fixed world props that players operate.
    ///
    /// Reported in play as soon as structure decoration started arriving: a lifepod's heat
    /// toggle and shower button worked per peer, so two players standing in the same pod
    /// had different temperatures and one could run a shower the other could not see.
    ///
    /// There is a single funnel for "the player used this thing" —
    /// <c>usableObject.gameObject.SendMessage("OnUse")</c> — and it is tempting to hook
    /// that the way <c>SetBlock</c> is hooked for terrain. **Do not.** Of the ten classes
    /// implementing <c>OnUse</c>, most open a screen belonging to the player who pressed
    /// it, and <c>Openable.OnUse</c> starts the lockpick or keypad *minigame*: replaying
    /// that on a peer would open a minigame on their screen because you opened a crate.
    /// Only props whose <c>OnUse</c> is purely world state may be replicated, and each one
    /// has to be read before it is added.
    ///
    /// <c>LifepodButton</c> is the first, and currently the only one examined and found
    /// safe: it does nothing but call <c>ToggleHeatState</c> or <c>ActivateShower</c> on
    /// its controller.
    /// </summary>
    internal static class PropSync
    {
        /// <summary>
        /// Carried in the interaction's <c>Slot</c> byte to mean "the shower was run",
        /// which is a momentary action rather than a state. Chosen outside the range of
        /// any heat state so the two cannot be confused.
        /// </summary>
        internal const byte ShowerMarker = 255;

        /// <summary>
        /// Reads the state a button has just put its controller into, or
        /// <see cref="ShowerMarker"/> for the shower.
        /// </summary>
        internal static byte StateOf(LifepodButton button)
        {
            if (button.type != 0) return ShowerMarker;
            var controller = button.controller;
            return controller == null ? (byte)0 : (byte)Mathf.Clamp(controller.heatState, 0, 2);
        }

        /// <summary>
        /// How far from the announced position a button may be and still be the one meant.
        ///
        /// Generous relative to what is actually needed — structure placement is
        /// deterministic, so the two positions agree exactly — but tight enough that two
        /// buttons on the same panel cannot be confused. They sit further apart than this.
        /// </summary>
        private const float MatchRadius = 0.4f;

        /// <summary>
        /// Finds the button a peer operated, by where it stands.
        ///
        /// By position rather than by entity id, because a lifepod's buttons turned out to
        /// have no id on either peer: they are part of the structure's tile decoration, so
        /// <c>GenerateEntityAtPos</c> re-parents them to <c>worldGrid</c>, which neither
        /// the scene-root sweep nor the chunk-child sweep looks at. Position is sound here
        /// precisely because that decoration is replayed deterministically — both peers
        /// build it from the same prefab at the same coordinates.
        /// </summary>
        internal static LifepodButton FindAt(Vector2 position)
        {
            LifepodButton best = null;
            float bestDistance = MatchRadius;

            foreach (var button in UnityEngine.Object.FindObjectsOfType<LifepodButton>())
            {
                if (button == null) continue;
                float d = Vector2.Distance(position, button.transform.position);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = button;
            }
            return best;
        }

        /// <summary>
        /// Brings a peer's copy of a prop to the state its operator left it in.
        ///
        /// The heat state is reached by calling <c>ToggleHeatState</c> until it matches
        /// rather than by assigning <c>heatState</c> directly. That method also sets the
        /// heater's target temperature, the sprite colours and the button's description
        /// text; driving it is how all of that comes along without this file having to
        /// know any of it. It cycles 0 → 1 → 2 → 0, so it always converges in under three
        /// steps; the guard is only there so a future change to the cycle cannot hang the
        /// game.
        ///
        /// Note this never calls <c>OnUse</c>, which is what keeps it from bouncing back
        /// out through the patch below.
        /// </summary>
        internal static void Apply(Vector2 position, byte state, Action<string> log)
        {
            var button = FindAt(position);
            if (button == null || button.controller == null)
            {
                log?.Invoke("[prop] no lifepod button near " + position.x.ToString("F1") + "," +
                            position.y.ToString("F1") + "; state not applied");
                return;
            }

            try
            {
                if (state == ShowerMarker)
                {
                    button.controller.ActivateShower();
                    log?.Invoke("[prop] shower run by a peer");
                    return;
                }

                int guard = 8;
                while (button.controller.heatState != state && guard-- > 0)
                    button.controller.ToggleHeatState();

                log?.Invoke("[prop] heat state is now " + button.controller.heatState +
                            " (peer set " + state + ")");
            }
            catch (Exception e)
            {
                log?.Invoke("[prop] could not apply state " + state + ": " + e.Message);
            }
        }
    }

    /// <summary>
    /// Publishes a lifepod button the local player just pressed.
    ///
    /// A postfix, so the state read is the one the press produced.
    /// </summary>
    [HarmonyPatch(typeof(LifepodButton), nameof(LifepodButton.OnUse))]
    internal static class LifepodButtonPatch
    {
        private static void Postfix(LifepodButton __instance)
        {
            try
            {
                var runner = CoopRunner.Active;
                if (runner == null || !runner.InSession) return;

                runner.AnnouncePropUse(__instance);
            }
            catch (Exception e)
            {
                CoopPlugin.Warn("prop patch error: " + e.Message);
            }
        }
    }
}
