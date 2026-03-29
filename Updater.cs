using System;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace VolumeMixer
{
    public class UpdateInfo
    {
        public string LatestVersion { get; set; }   // e.g. "v1.2.0"
        public string DownloadUrl { get; set; }    // browser download page
        public string ReleaseNotes { get; set; }
    }

    public static class Updater
    {
        // ── Change these if you ever rename the repo ──────────────────────────
        private const string Owner = "generallouay";
        private const string Repo = "custom-volume-mixer";
        private const string ApiUrl = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

        // Registry key for "never ask again"
        private const string REG_KEY = @"Software\VolumeMixer";
        private const string REG_SKIP_VER = "SkipUpdateVersion";

        // ── Current app version — bump this with every release ────────────────
        public static readonly Version CurrentVersion = new Version(2, 0, 1);

        // Returns null if up to date, network error, or user said never.
        public static UpdateInfo CheckForUpdate()
        {
            // Has the user said "never ask again" for this version?
            string skipVer = ReadSkipVersion();

            try
            {
                string json = FetchJson(ApiUrl);
                string tag = ExtractField(json, "tag_name");   // "v1.2.0"
                string html = ExtractField(json, "html_url");   // release page
                string body = ExtractField(json, "body");       // release notes

                // Strip leading 'v'
                string numericTag = tag.TrimStart('v');
                if (!Version.TryParse(numericTag, out Version latest)) return null;

                if (latest <= CurrentVersion) return null;

                // User said never for this exact version
                if (skipVer == tag) return null;

                return new UpdateInfo
                {
                    LatestVersion = tag,
                    DownloadUrl = html,
                    ReleaseNotes = body
                };
            }
            catch { return null; }  // Silently fail — no internet, rate limit, etc.
        }

        // Direct download URL pattern for GitHub release assets
        // e.g. https://github.com/owner/repo/releases/download/v1.1.0/VolumeMixer-Setup-v1.1.0.exe
        public static string GetInstallerUrl(string tag)
        {
            // Strip leading 'v' for the filename portion
            string ver = tag.TrimStart('v');
            return $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/VolumeMixer-Setup-v{ver}.exe";
        }

        // Downloads the installer to %TEMP%, runs it, exits the app.
        // Progress callback receives 0-100. Call from a background thread.
        public static void DownloadAndInstall(string tag, Action<int> onProgress, Action<string> onError)
        {
            string url = GetInstallerUrl(tag);
            string dest = Path.Combine(Path.GetTempPath(), $"VolumeMixer-Setup-{tag}.exe");

            try
            {
                using (var client = new WebClient())
                {
                    client.DownloadProgressChanged += (s, e) => onProgress?.Invoke(e.ProgressPercentage);
                    client.DownloadFileCompleted += (s, e) =>
                    {
                        if (e.Error != null) { onError?.Invoke(e.Error.Message); return; }
                        if (e.Cancelled) { onError?.Invoke("Cancelled."); return; }

                        // Get our own exe path to pass to the installer so it can relaunch us
                        string appExe = System.Windows.Forms.Application.ExecutablePath;

                        var psi = new ProcessStartInfo(dest,
                            "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /RESTARTEXIT /RELAUNCHPATH=\"" + appExe + "\"")
                        {
                            UseShellExecute = true
                        };
                        Process.Start(psi);

                        System.Windows.Forms.Application.Exit();
                    };
                    client.DownloadFileAsync(new Uri(url), dest);
                }
            }
            catch (Exception ex) { onError?.Invoke(ex.Message); }
        }

        public static void SaveSkipVersion(string version)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REG_KEY))
                    key.SetValue(REG_SKIP_VER, version);
            }
            catch { }
        }

        private static string ReadSkipVersion()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_KEY))
                    return key?.GetValue(REG_SKIP_VER) as string ?? "";
            }
            catch { return ""; }
        }

        // ── Minimal JSON field extractor (no external deps) ───────────────────
        private static string ExtractField(string json, string field)
        {
            string search = "\"" + field + "\"";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return "";
            idx += search.Length;
            // skip whitespace and colon
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            if (idx >= json.Length) return "";

            if (json[idx] == '"')
            {
                // String value
                idx++;
                var sb = new StringBuilder();
                while (idx < json.Length && json[idx] != '"')
                {
                    if (json[idx] == '\\') idx++; // skip escape char
                    if (idx < json.Length) sb.Append(json[idx]);
                    idx++;
                }
                return sb.ToString();
            }
            // Non-string (number/bool/null) — unlikely to be needed here
            int end = json.IndexOfAny(new[] { ',', '}', ']' }, idx);
            return end < 0 ? json.Substring(idx) : json.Substring(idx, end - idx).Trim();
        }

        private static string FetchJson(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "VolumeMixer-Updater/1.0"; // GitHub API requires a UA
            req.Timeout = 5000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}