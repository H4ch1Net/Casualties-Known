using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// The End lab (layer 12) as a team. The game runs the whole sequence on each machine
    /// for its own player: the scientist, the escape, the move into the surgery room, the
    /// 60.75 s escape timer, the cargo surgery and the ending, and that stays so: each
    /// player gets their own surgery and their own win. Two things need the team:
    ///
    /// - Shooting the scientist already starts the escape everywhere (every shot is
    ///   replayed, and the game starts the escape from any shot that hits him).
    /// - The end guards only spawn on the host, but each machine's timer releases them
    ///   on that machine. The first timer to run out anywhere now releases them on the
    ///   host's spawner, which sends a guard to whichever player is in range.
    /// - A host who finishes first must not end the run for the others (see
    ///   <see cref="HostLeaveGuardPatch"/>).
    /// </summary>
    [HarmonyPatch(typeof(EndLabScript), "Update")]
    internal static class EscapeTimerPatch
    {
        private static readonly AccessTools.FieldRef<EndLabScript, float> TimeLeft = AccessTools.FieldRefAccess<EndLabScript, float>("timeLeft");

        private static void Prefix(EndLabScript __instance, out bool __state) => __state = __instance.doTimer;

        private static void Postfix(EndLabScript __instance, bool __state)
        {
            // The timer just ran out here (the brain-dying ending also stops it, with time left).
            if (!__state || __instance.doTimer || TimeLeft(__instance) > 0f || __instance.endGuardSpawner == null) return;
            if (!Coop.WorldLive) return;
            Log.Info("[endlab] the escape timer ran out here; the end guards come for the team");
            WorldEvents.Fire(WorldEventKind.EndGuards, __instance.endGuardSpawner);
        }
    }

    /// <summary>
    /// The game's end screen goes back to the menu only when its player clicks. A host
    /// who has finished and clicks while teammates are still in the run would end it for
    /// them, so the first click is held with a warning and a second one within 8 s leaves.
    /// The mod's own returns to the menu are not held.
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ToMainMenu))]
    internal static class HostLeaveGuardPatch
    {
        internal static bool Bypass;
        private static float _armedUntil = -1f;

        private static bool Prefix()
        {
            if (Bypass || !Coop.IsHost || !Coop.WorldLive || !UI.TeamReceipt.EndScreenUp) return true;
            int playing = 0;
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != Coop.LocalId && rec.InWorld && !rec.Finished) playing++;
            if (playing == 0 || Time.unscaledTime < _armedUntil) return true;
            _armedUntil = Time.unscaledTime + 8f;
            CoopRuntime.Notice((playing == 1 ? "A teammate is" : playing + " teammates are") +
                               " still playing. Leaving now ends the run for everyone: do it again within 8 s to leave anyway.");
            Log.Info("[endlab] held the host's return to the menu: " + playing + " still playing");
            return false;
        }
    }
}
