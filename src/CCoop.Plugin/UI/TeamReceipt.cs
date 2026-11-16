using System.Collections.Generic;
using System.Text;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.Sync;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The co-op line on the game's end screen: the game prints a receipt for your own
    /// character; this adds the team, each with their character, how far they got and,
    /// for those who died, the likely cause, read from their last vital signs. A button
    /// copies the run summary, like the game's own clipboard copy.
    /// </summary>
    internal sealed class TeamReceipt : CoopModule
    {
        private sealed class Fate
        {
            public string Name = "";
            public int Character = -1;
            public int Layer;
            public bool Dead;
            public string Cause = "";
        }

        private static readonly Dictionary<byte, Fate> Fates = new Dictionary<byte, Fate>();
        private static float _showFrom = -1f;
        private static GUIStyle _style;

        internal override void Detach()
        {
            Fates.Clear();
            _showFrom = -1f;
            ReachedEnding = false;
        }

        /// <summary>From the game's EndSequence: the run is over for this player.</summary>
        /// <param name="type">The game's end screen kind: 0 is death, 1 bio-pod, 2 escape, 3 M.E.R.C.</param>
        internal static void EndScreenShown(int type = 1)
        {
            if (!Coop.InSession) return;
            Refresh();
            _showFrom = Time.unscaledTime + 4f;  // after the game's receipt has come in
            ReachedEnding = type != 0;
            if (!ReachedEnding) return;
            Coop.Player(Coop.LocalId).Finished = true;
            if (Coop.IsHost)
                foreach (var rec in Coop.Players.Values)
                    if (rec.Id != Coop.LocalId && rec.InWorld && !rec.Finished && rec.Alive)
                    {
                        CoopRuntime.Notice(Lang.T("Your teammates are still playing. Stay on this screen until they finish: leaving ends the run for everyone."));
                        break;
                    }
        }

        /// <summary>This player is on the game's end screen (an ending or death).</summary>

        /// <summary>This player reached an ending (not death) and is on the end screen.</summary>
        internal static bool ReachedEnding { get; private set; }

        internal override void Tick()
        {
            if (GameInfo.OnMainMenu)
            {
                _showFrom = -1f;
                ReachedEnding = false;
                foreach (var rec in Coop.Players.Values) rec.Finished = false;  // a new run starts unfinished
                return;
            }
            if (!Coop.WorldLive) return;
            Refresh();
        }

        private static void Refresh()
        {
            var w = WorldGeneration.world;
            int layer = w != null ? w.biomeDepth + 1 : 0;
            foreach (var rec in Coop.Players.Values)
            {
                if (!Fates.TryGetValue(rec.Id, out var f)) Fates[rec.Id] = f = new Fate();
                f.Name = rec.Id == Coop.LocalId ? rec.Name + " (" + Lang.T("you") + ")" : rec.Name;
                f.Character = rec.Id == Coop.LocalId && GameInfo.LocalBody != null ? GameInfo.LocalBody.charType : rec.Character;
                bool alive = rec.Id == Coop.LocalId ? GameInfo.LocalBody != null && GameInfo.LocalBody.alive : rec.Alive;
                if (alive)
                {
                    f.Dead = false;
                    f.Layer = layer;
                    f.Cause = "";
                }
                else if (!f.Dead)
                {
                    f.Dead = true;
                    f.Layer = layer;
                    f.Cause = rec.Id == Coop.LocalId ? CauseOf(VitalsSync.Read(GameInfo.LocalBody, 0f)) : CauseOf(Coop.Get<VitalsSync>()?.Of(rec.Id));
                }
            }
        }

        /// <summary>A plain-words guess at what killed someone, from their last vital signs.</summary>
        private static string CauseOf(Vitals v)
        {
            if (v == null) return "";
            float temperature = 20f + v.Temperature / 10f;
            if (temperature < 31f) return Lang.N("froze");
            if (temperature > 41.5f) return Lang.N("overheated");
            if (v.Blood < 35) return Lang.N("bled out");
            if (v.Oxygen < 45) return Lang.N("ran out of air");
            if ((v.Signs & BodySigns.Arrest) != 0 || v.Fibrillation > 60) return Lang.N("heart stopped");
            return "";
        }

        private static string Line(Fate f) =>
            f.Name + " · " + GameInfo.CharacterName(f.Character) + " · " +
            (f.Dead ? Lang.F("died on layer {0}", f.Layer) + (f.Cause.Length > 0 ? ", " + Lang.T(f.Cause) : "") : Lang.F("alive on layer {0}", f.Layer));

        internal static string Summary()
        {
            var sb = new StringBuilder(Lang.F("CasualtiesCoop run · team of {0}", Fates.Count) + "\n");
            foreach (var f in Fates.Values) sb.Append(Line(f)).Append('\n');
            return sb.ToString();
        }

        internal override void Gui()
        {
            if (_showFrom < 0f || Time.unscaledTime < _showFrom || Fates.Count < 2) return;
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Body, wordWrap = true };
            float a = Mathf.Clamp01((Time.unscaledTime - _showFrom) / 1.5f);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a);
            float w = 400f, h = 78f + 24f * Fates.Count;
            var r = new Rect(Screen.width - w - 24f, 24f, w, h);
            GUI.Box(r, GUIContent.none);
            GUILayout.BeginArea(new Rect(r.x + 10f, r.y + 8f, r.width - 20f, r.height - 12f));
            GUILayout.Label(Lang.T("Your team"), _style);
            foreach (var f in Fates.Values) GUILayout.Label(Line(f), _style);
            if (GUILayout.Button(Lang.T("Copy run summary"), GUILayout.Width(150))) GUIUtility.systemCopyBuffer = Summary();
            GUILayout.EndArea();
            GUI.color = old;
        }
    }

    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.EndSequence))]
    internal static class EndScreenPatch
    {
        private static void Prefix(int type) => TeamReceipt.EndScreenShown(type);
    }
}
