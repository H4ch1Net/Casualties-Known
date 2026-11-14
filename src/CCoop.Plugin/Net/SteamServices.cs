using System;
using System.Collections.Generic;
using CCoop.Plugin.Runtime;
using Steamworks;
using UnityEngine;

namespace CCoop.Plugin.Net
{
    /// <summary>
    /// Small Steam conveniences: names, avatars, ping estimates, Steam Deck detection,
    /// "recent players", and Steam game-recording (timeline) markers. Everything is a
    /// no-op when Steam is not running, so callers never need to check.
    /// </summary>
    internal static class SteamServices
    {
        internal static bool Ready => GameInfo.SteamReady;

        // ------------------------------------------------------------------ names and avatars

        private static readonly Dictionary<ulong, Texture2D> Avatars = new Dictionary<ulong, Texture2D>();
        private static readonly HashSet<ulong> Requested = new HashSet<ulong>();
        private static Callback<AvatarImageLoaded_t> _avatarLoaded;
        private static Callback<PersonaStateChange_t> _personaChanged;

        private static void EnsureCallbacks()
        {
            if (_avatarLoaded != null || !Ready) return;
            _avatarLoaded = Callback<AvatarImageLoaded_t>.Create(r => Requested.Remove(r.m_steamID.m_SteamID));
            _personaChanged = Callback<PersonaStateChange_t>.Create(r => Requested.Remove(r.m_ulSteamID));
        }

        internal static string Name(ulong id)
        {
            if (!Ready || id == 0) return null;
            try
            {
                var name = SteamFriends.GetFriendPersonaName(new CSteamID(id));
                return string.IsNullOrEmpty(name) || name == "[unknown]" ? null : name;
            }
            catch { return null; }
        }

        /// <summary>A 64x64 avatar, or null while Steam is still fetching it.</summary>
        internal static Texture2D Avatar(ulong id)
        {
            if (!Ready || id == 0) return null;
            if (Avatars.TryGetValue(id, out var tex)) return tex;
            EnsureCallbacks();
            if (Requested.Contains(id)) return null;
            try
            {
                var steamId = new CSteamID(id);
                int handle = SteamFriends.GetMediumFriendAvatar(steamId);
                if (handle == -1 || handle == 0)
                {
                    // -1: still loading (AvatarImageLoaded follows). 0: unknown user; ask.
                    if (handle == 0) SteamFriends.RequestUserInformation(steamId, false);
                    Requested.Add(id);
                    return null;
                }
                tex = Load(handle);
                if (tex != null) Avatars[id] = tex;
                return tex;
            }
            catch { return null; }
        }

        private static Texture2D Load(int handle)
        {
            if (!SteamUtils.GetImageSize(handle, out uint w, out uint h) || w == 0 || h == 0) return null;
            var raw = new byte[w * h * 4];
            if (!SteamUtils.GetImageRGBA(handle, raw, raw.Length)) return null;
            // Steam rows run top to bottom; Unity textures bottom to top.
            var flipped = new byte[raw.Length];
            int row = (int)w * 4;
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(raw, y * row, flipped, ((int)h - 1 - y) * row, row);
            var tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            tex.LoadRawTextureData(flipped);
            tex.Apply();
            return tex;
        }

        /// <summary>Puts a teammate in Steam's "Recent players" list.</summary>
        internal static void PlayedWith(ulong id)
        {
            if (!Ready || id == 0 || id == GameInfo.SteamId) return;
            try { SteamFriends.SetPlayedWith(new CSteamID(id)); }
            catch { /* cosmetic */ }
        }

        // ------------------------------------------------------------------ ping estimates

        /// <summary>This machine's relay ping location, to publish in lobby data. Null until known.</summary>
        internal static string PingLocation()
        {
            if (!Ready) return null;
            try
            {
                if (SteamNetworkingUtils.GetLocalPingLocation(out var loc) < 0f) return null;
                SteamNetworkingUtils.ConvertPingLocationToString(ref loc, out var text, 512);
                return text;
            }
            catch { return null; }
        }

        /// <summary>Estimated round trip from here to a published ping location, or -1.</summary>
        internal static int EstimatePing(string location)
        {
            if (!Ready || string.IsNullOrEmpty(location)) return -1;
            try
            {
                if (!SteamNetworkingUtils.ParsePingLocationString(location, out var loc)) return -1;
                return SteamNetworkingUtils.EstimatePingTimeFromLocalHost(ref loc);
            }
            catch { return -1; }
        }

        // ------------------------------------------------------------------ platform

        internal static bool OnSteamDeck
        {
            get
            {
                if (!Ready) return false;
                try { return SteamUtils.IsSteamRunningOnSteamDeck(); }
                catch { return false; }
            }
        }

        private static int _deck = -1;

        /// <summary>Cached: Deck detection does not change while the game runs.</summary>
        internal static bool OnDeckCached
        {
            get
            {
                if (_deck < 0 && Ready) _deck = OnSteamDeck ? 1 : 0;
                return _deck == 1;
            }
        }

        /// <summary>Steam can show its on-screen keyboard here (Deck, Big Picture).</summary>
        internal static bool OnScreenKeyboard
        {
            get
            {
                if (!Ready) return false;
                if (OnDeckCached) return true;
                try { return SteamUtils.IsSteamInBigPictureMode(); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Opens Steam's floating keyboard next to a text field (screen pixels, top-left
        /// origin). Keys arrive as ordinary input, so IMGUI fields just work.
        /// </summary>
        internal static void ShowKeyboard(Rect field)
        {
            if (!Ready) return;
            try
            {
                SteamUtils.ShowFloatingGamepadTextInput(
                    EFloatingGamepadTextInputMode.k_EFloatingGamepadTextInputModeModeSingleLine,
                    (int)field.x, (int)field.y, (int)field.width, (int)field.height);
            }
            catch (Exception e) { Log.Debug("keyboard: " + e.Message); }
        }

        internal static uint AppBuildId
        {
            get
            {
                if (!Ready) return 0;
                try { return (uint)SteamApps.GetAppBuildId(); }
                catch { return 0; }
            }
        }

        // ------------------------------------------------------------------ game recording

        /// <summary>Marks a moment on Steam's game-recording timeline.</summary>
        internal static void TimelineEvent(string title, string description, string icon, bool featured = false)
        {
            if (!Ready) return;
            try
            {
                SteamTimeline.AddInstantaneousTimelineEvent(title, description ?? "", icon, 0u, 0f,
                    featured ? ETimelineEventClipPriority.k_ETimelineEventClipPriority_Featured
                             : ETimelineEventClipPriority.k_ETimelineEventClipPriority_Standard);
            }
            catch (Exception e) { Log.Debug("timeline: " + e.Message); }
        }

        internal static void TimelineMode(ETimelineGameMode mode)
        {
            if (!Ready) return;
            try { SteamTimeline.SetTimelineGameMode(mode); }
            catch (Exception e) { Log.Debug("timeline mode: " + e.Message); }
        }

        /// <summary>Starts a recording phase (one per co-op run) with a layer attribute.</summary>
        internal static void TimelineStartRun(string id)
        {
            if (!Ready) return;
            try
            {
                SteamTimeline.StartGamePhase();
                SteamTimeline.SetGamePhaseID(id);
            }
            catch (Exception e) { Log.Debug("timeline phase: " + e.Message); }
        }

        internal static void TimelineLayer(string layerTitle, uint priority)
        {
            if (!Ready) return;
            try
            {
                SteamTimeline.SetGamePhaseAttribute("Layer", layerTitle, priority);
                SteamTimeline.AddGamePhaseTag(layerTitle, "steam_flag", "Layer", priority);
            }
            catch (Exception e) { Log.Debug("timeline layer: " + e.Message); }
        }

        internal static void TimelineEndRun()
        {
            if (!Ready) return;
            try { SteamTimeline.EndGamePhase(); }
            catch (Exception e) { Log.Debug("timeline end: " + e.Message); }
        }
    }
}
