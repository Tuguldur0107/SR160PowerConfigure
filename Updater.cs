using System;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace SR160PowerConfig
{
    internal sealed class UpdateInfo
    {
        public Version Version;
        public string Tag;
        public string Notes;
        public string DownloadUrl;
    }

    // Checks the project's GitHub releases for a newer build. Read-only and
    // entirely optional — every failure path returns null so that no network
    // problem, rate limit or API change can stop the app starting.
    internal static class Updater
    {
        private const string LatestReleaseApi =
            "https://api.github.com/repos/Tuguldur0107/SR160PowerConfigure/releases/latest";

        // Matches the installer asset published on each release. The installer
        // is fetched rather than the bare exe because it also refreshes the
        // native DLLs and the shortcuts.
        private const string AssetName = "SR160PowerConfig_Setup.exe";

        public static Version CurrentVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static UpdateInfo CheckForUpdate()
        {
            try
            {
                // .NET 4.0 negotiates SSL3/TLS1.0 by default and GitHub refuses
                // both, so TLS 1.2 has to be requested explicitly. The enum
                // member doesn't exist in the 4.0 reference assemblies, hence
                // the numeric cast (3072 = Tls12, 768 = Tls11, 192 = Tls).
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); }
                catch { }

                string json;
                using (WebClient client = new WebClient())
                {
                    // GitHub rejects API requests that omit a User-Agent.
                    client.Headers.Add("User-Agent", "SR160PowerConfig");
                    client.Headers.Add("Accept", "application/vnd.github+json");
                    // Must be set explicitly: WebClient otherwise decodes with
                    // the system ANSI codepage, which turns non-ASCII release
                    // notes (Cyrillic here) into mojibake.
                    client.Encoding = System.Text.Encoding.UTF8;
                    json = client.DownloadString(LatestReleaseApi);
                }
                if (string.IsNullOrEmpty(json)) return null;

                string tag = Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (string.IsNullOrEmpty(tag)) return null;

                Version latest = ParseVersion(tag);
                if (latest == null) return null;
                if (latest <= CurrentVersion) return null;

                string url = Match(json,
                    "\"browser_download_url\"\\s*:\\s*\"([^\"]*" + Regex.Escape(AssetName) + ")\"");
                if (string.IsNullOrEmpty(url)) return null;

                UpdateInfo info = new UpdateInfo();
                info.Version = latest;
                info.Tag = tag;
                info.DownloadUrl = url;
                info.Notes = CleanNotes(Unescape(Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")));
                return info;
            }
            catch
            {
                // Offline, rate limited, no releases yet, JSON shape changed —
                // all of it means "carry on without updating".
                return null;
            }
        }

        public static string Download(string url)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), AssetName);
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); }
            catch { }
            using (WebClient client = new WebClient())
            {
                client.Headers.Add("User-Agent", "SR160PowerConfig");
                client.DownloadFile(url, path);
            }
            return path;
        }

        private static string Match(string input, string pattern)
        {
            Match m = Regex.Match(input, pattern, RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        // Release tags look like "v1.3" or "1.3.1"; Version wants at least
        // major.minor, so a bare "v2" is padded rather than rejected.
        //
        // Padded to all FOUR components on purpose. Version leaves omitted
        // components at -1, not 0, and -1 sorts BELOW 0 — so a "v1.2" tag
        // parsed as 1.2.-1.-1 compares as older than an installed 1.2.0.0
        // and the release is silently skipped. That happened to the real v1.2
        // release: no 1.2.0.0 build could ever see it.
        private static Version ParseVersion(string tag)
        {
            string cleaned = tag.Trim();
            if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(1);
            Match m = Regex.Match(cleaned, @"^\d+(\.\d+){0,3}");
            if (!m.Success) return null;
            cleaned = m.Value;
            for (int parts = cleaned.Split('.').Length; parts < 4; parts++)
                cleaned += ".0";
            try { return new Version(cleaned); }
            catch { return null; }
        }

        // Release notes are markdown, which reads badly raw in a message box —
        // heading hashes, bold markers and table pipes are noise to the person
        // deciding whether to update. Strip the syntax and drop table rows,
        // keeping the plain prose and bullet points.
        private static string CleanNotes(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";
            string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("|")) continue;            // table row
                if (Regex.IsMatch(line, @"^[-:\s|]+$")) continue; // table rule / divider
                line = Regex.Replace(line, @"^#{1,6}\s*", "");  // heading marker
                line = line.Replace("**", "").Replace("`", "");
                if (line.Length == 0) continue;
                sb.AppendLine(line);
            }
            return sb.ToString().Trim();
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\r\\n", "\n").Replace("\\n", "\n")
                    .Replace("\\r", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
