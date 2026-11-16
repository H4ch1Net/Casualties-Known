using CCoop.Plugin.Runtime;
using CCoop.Plugin.Sync;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The co-op keys, told once: a few seconds into the first co-op run after the game
    /// starts, one line at a time on the game's own alert line. Off in the co-op options.
    /// </summary>
    internal sealed class RunTips : CoopModule
    {
        private const float FirstAfter = 6f, Between = 6.5f;

        private static bool _done;
        private float _liveAt = -1f;
        private int _next;

        internal override void OnWorldLive() => _liveAt = Time.unscaledTime;

        internal override void OnWorldLost() => _liveAt = -1f;

        internal override void Tick()
        {
            if (_done || _liveAt < 0f || !CoopPlugin.Ready || !Coop.WorldLive) return;
            if (!CoopPlugin.Instance.Tips.Value) { _done = true; return; }
            var body = GameInfo.LocalBody;
            if (body == null || !body.alive) return;
            if (Time.unscaledTime < _liveAt + FirstAfter + _next * Between) return;

            var cfg = CoopPlugin.Instance;
            var interact = KeyBindsBlockPatch.Raw("iteminteract");
            string line;
            switch (_next)
            {
                case 0:
                    line = interact == KeyCode.Mouse1
                        ? Lang.T("Right-click a teammate to carry, drag, give or treat them.")
                        : Lang.F("Point at a teammate and press {0} to carry, drag, give or treat them.", UiSkin.KeyName(interact));
                    break;
                case 1:
                    line = Lang.F("Middle click pings a spot. Hold {0} for gestures, {1} to chat.",
                                  UiSkin.KeyName(cfg.GestureKey.Value), UiSkin.KeyName(cfg.ChatKey.Value));
                    break;
                default:
                    line = Lang.F("All the co-op keys are in the co-op menu ({0}), under Options.", UiSkin.KeyName(cfg.MenuKey.Value));
                    _done = true;
                    break;
            }
            _next++;
            Log.Info("[tips] " + line);
            CoopRuntime.Notice(line);
        }
    }
}
