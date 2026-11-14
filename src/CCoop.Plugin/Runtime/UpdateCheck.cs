using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace CCoop.Plugin.Runtime
{
    /// <summary>
    /// Once per launch, asks GitHub for the latest release of the configured repository
    /// and, if it's newer, says so in the co-op window. Nothing is downloaded or installed,
    /// and no player data is sent: it's one anonymous request to GitHub's public API.
    /// Off until <c>Update.Repository</c> is set (e.g. "owner/CasualtiesCoop").
    /// </summary>
    internal static class UpdateCheck
    {
        internal static string Notice = "";

        internal static IEnumerator Run()
        {
            var cfg = CoopPlugin.Instance;
            if (cfg == null || !cfg.UpdateCheckEnabled.Value) yield break;
            string repo = (cfg.UpdateRepository.Value ?? "").Trim();
            if (repo.Length == 0 || repo.IndexOf('/') <= 0) yield break;

            using (var req = UnityWebRequest.Get("https://api.github.com/repos/" + repo + "/releases/latest"))
            {
                req.SetRequestHeader("User-Agent", "CasualtiesCoop");
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 10;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Log.Info("update check: " + req.error);
                    yield break;
                }
                string tag = Field(req.downloadHandler.text, "tag_name");
                string url = Field(req.downloadHandler.text, "html_url");
                if (IsNewer(tag, CoopPlugin.Version))
                {
                    Notice = "CasualtiesCoop " + tag.TrimStart('v', 'V') + " is out (you have " + CoopPlugin.Version + "): " + url;
                    Log.Info("update check: " + Notice);
                }
                else Log.Info("update check: up to date (" + tag + ")");
            }
        }

        /// <summary>A top-level string field from GitHub's JSON, without a JSON library.</summary>
        private static string Field(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string key = "\"" + name + "\"";
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return "";
            int colon = json.IndexOf(':', at + key.Length);
            int open = json.IndexOf('"', colon + 1);
            int close = open >= 0 ? json.IndexOf('"', open + 1) : -1;
            return open >= 0 && close > open ? json.Substring(open + 1, close - open - 1) : "";
        }

        internal static bool IsNewer(string tag, string current)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            return Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest) &&
                   Version.TryParse(current, out var mine) && latest > mine;
        }
    }
}
