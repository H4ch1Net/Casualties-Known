using System;
using BepInEx.Logging;
using HarmonyLib;

namespace CCoop.Plugin
{
    /// <summary>
    /// Blocks vanilla saves while a co-op session is live.
    ///
    /// Why this is not optional:
    ///
    ///   * <c>SaveSystem.SaveGame()</c> is a parameterless static writing to a SINGLE
    ///     global slot — there is no slot argument to redirect.
    ///   * Unity derives <c>persistentDataPath</c> from company/product name rather than
    ///     install path, so two instances on one machine share that slot even if the
    ///     game is installed twice. Copying the game folder does not help.
    ///
    /// So two peers saving at once do not produce two saves; they interleave writes into
    /// one file. Worse, a co-op save is not a valid single-player save anyway: the
    /// client's body state is authoritative on the host, not locally.
    ///
    /// Until M6 implements host-only namespaced saves, the safe behaviour is to refuse.
    /// Refusing is recoverable; a corrupted single-player save is not.
    /// </summary>
    internal static class SaveGuard
    {
        private static Harmony _harmony;
        private static ManualLogSource _log;

        /// <summary>Set by the runner while a session is active.</summary>
        internal static bool SessionActive;

        /// <summary>Counts blocked attempts, surfaced in the overlay.</summary>
        internal static int BlockedSaves { get; private set; }

        internal static void Install(ManualLogSource log)
        {
            if (_harmony != null) return;
            _log = log;

            try
            {
                _harmony = new Harmony(CoopPlugin.Guid + ".saveguard");

                var target = AccessTools.Method(typeof(SaveSystem), nameof(SaveSystem.SaveGame));
                if (target == null)
                {
                    // Never fail silently: without this patch, a co-op session can
                    // destroy the player's single-player save.
                    log.LogError("SaveGuard: SaveSystem.SaveGame not found — vanilla saves are NOT protected. " +
                                 "Do not run two instances until this is resolved.");
                    return;
                }

                var prefix = AccessTools.Method(typeof(SaveGuard), nameof(BlockSaveDuringCoop));
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                log.LogInfo("SaveGuard installed: vanilla saves are blocked during co-op sessions.");
            }
            catch (Exception e)
            {
                log.LogError("SaveGuard failed to install (vanilla saves NOT protected): " + e);
            }
        }

        internal static void Uninstall()
        {
            try { _harmony?.UnpatchSelf(); } catch { /* shutting down */ }
            _harmony = null;
        }

        /// <summary>
        /// Harmony prefix. Returning false skips the original method.
        /// </summary>
        private static bool BlockSaveDuringCoop()
        {
            if (!SessionActive) return true; // normal single-player save proceeds

            BlockedSaves++;
            _log?.LogWarning(
                "Blocked a vanilla save while a co-op session is active (attempt #" + BlockedSaves + "). " +
                "The game has one global save slot shared by all instances, so saving here would " +
                "corrupt the single-player save. Co-op saves arrive in a later milestone.");
            return false;
        }
    }
}
