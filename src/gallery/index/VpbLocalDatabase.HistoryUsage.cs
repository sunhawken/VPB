using System;
using MVR.FileManagement;

namespace VPB
{
    /// <summary>
    /// History / <c>item_usage</c> helpers for VaM-native load paths (file browser, Scene Loader triggers)
    /// in addition to explicit gallery UI records.
    /// </summary>
    internal static partial class VpbLocalDatabase
    {
        private const float HistoryRecordDedupeSeconds = 2.5f;
        private static string _lastHistoryRecordKey = "";
        private static string _lastHistoryRecordKind = "";
        private static DateTime _lastHistoryRecordUtc = DateTime.MinValue;

        /// <summary>Normalize a VaM load path / UID into an <c>item_usage.item_key</c>.</summary>
        internal static string BuildUsageKeyFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                string k = path;
                try
                {
                    string n = FileManager.NormalizePath(path);
                    if (!string.IsNullOrEmpty(n)) k = n;
                }
                catch { }
                if (string.IsNullOrEmpty(k)) return "";
                return CanonicalizeUsageKey(k.Replace('\\', '/').Trim().ToLowerInvariant());
            }
            catch
            {
                return "";
            }
        }

        private static readonly char[] s_UsageKeyPathSeparators = new[] { '/', '\\' };

        /// <summary>
        /// Collapse the package half of an <c>item_usage.item_key</c> to the canonical package UID.
        /// <para>
        /// The same package reaches this code under several spellings depending on whether it was
        /// registered when the key was built: the UID (<c>author.pkg.1:/…</c>), the archive file name
        /// (<c>author.pkg.1.var:/…</c>, <c>author.pkg.1.disabled:/…</c>, <c>author.pkg.1.var.disabled:/…</c>)
        /// or a full archive path. A <c>.var</c> disabled by its <c>.disabled</c> sidecar registers with no
        /// file entries at all, so <see cref="FileManager.NormalizePath"/> cannot resolve it and hands back
        /// the raw path. Left alone, one package accrues usage under several keys and none of them join
        /// <c>pkg</c>, so it is missing from History and from usage-count sort.
        /// </para>
        /// Keys that do not name a package (loose <c>Saves/</c> and <c>Custom/</c> files) are returned unchanged.
        /// </summary>
        internal static string CanonicalizeUsageKey(string itemKey)
        {
            if (string.IsNullOrEmpty(itemKey)) return itemKey ?? "";
            try
            {
                // Last separator, not first: a full Windows archive path carries a drive-letter ":/" too.
                int sep = itemKey.LastIndexOf(":/", StringComparison.Ordinal);
                if (sep <= 0) return itemKey;

                string prefix = itemKey.Substring(0, sep);
                int slash = prefix.LastIndexOfAny(s_UsageKeyPathSeparators);
                if (slash >= 0) prefix = prefix.Substring(slash + 1);
                if (prefix.Length == 0) return itemKey;

                string uid = StripPackageArchiveSuffixes(prefix);
                if (!LooksLikePackageUid(uid)) return itemKey;

                return uid.ToLowerInvariant() + ":/" + itemKey.Substring(sep + 2);
            }
            catch
            {
                return itemKey;
            }
        }

        /// <summary>Strips <c>.disabled</c>, then one <c>.var</c> / <c>.zip</c>, so <c>x.var.disabled</c> also reduces to <c>x</c>.</summary>
        private static string StripPackageArchiveSuffixes(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            string s = name;
            if (s.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 9);
            if (s.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                || s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);
            return s;
        }

        /// <summary>True for <c>Creator.Name.&lt;version|latest&gt;</c> — the shape <c>pkg.uid</c> stores.</summary>
        private static bool LooksLikePackageUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            string[] parts = uid.Split('.');
            if (parts.Length != 3) return false;
            if (parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) return false;
            if (string.Equals(parts[2], "latest", StringComparison.OrdinalIgnoreCase)) return true;
            for (int i = 0; i < parts[2].Length; i++)
                if (parts[2][i] < '0' || parts[2][i] > '9') return false;
            return true;
        }

        /// <summary>
        /// Skip ephemeral / auto loads that must not appear in History
        /// (temp merge scenes, default scene, empty browser callbacks).
        /// </summary>
        internal static bool ShouldSkipHistoryPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string p;
            try { p = path.Replace('\\', '/').Trim(); }
            catch { return true; }
            if (p.Length == 0) return true;
            if (string.Equals(p, "savefile", StringComparison.OrdinalIgnoreCase)) return true;

            string lower = p.ToLowerInvariant();
            if (lower.IndexOf("vpb_temp_", StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf("vpb_scene", StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf("vpb_filtered", StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf("vpb_rewrite", StringComparison.Ordinal) >= 0) return true;
            if (lower.EndsWith("saves/scene/meshedvr/default.json", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Map VaM preset storable id → History kind (matches gallery UI kinds).</summary>
        internal static string KindFromPresetStorableId(string storableId)
        {
            if (string.IsNullOrEmpty(storableId)) return "item";
            if (string.Equals(storableId, "Appearancepresets", StringComparison.OrdinalIgnoreCase)) return "appearance";
            if (string.Equals(storableId, "clothingpresets", StringComparison.OrdinalIgnoreCase)) return "clothing";
            if (string.Equals(storableId, "hairpresets", StringComparison.OrdinalIgnoreCase)) return "hair";
            if (string.Equals(storableId, "posepresets", StringComparison.OrdinalIgnoreCase)) return "pose";
            if (string.Equals(storableId, "pluginspresets", StringComparison.OrdinalIgnoreCase)) return "plugins";
            if (string.Equals(storableId, "skin", StringComparison.OrdinalIgnoreCase)) return "skin";
            if (string.Equals(storableId, "morphs", StringComparison.OrdinalIgnoreCase)) return "morphs";
            if (string.Equals(storableId, "animationpresets", StringComparison.OrdinalIgnoreCase)) return "item";
            if (string.Equals(storableId, "breastphysicspresets", StringComparison.OrdinalIgnoreCase)) return "item";
            return "item";
        }

        /// <summary>
        /// Record History for a VaM load path. No-ops for skip paths and near-duplicate
        /// (same key+kind within <see cref="HistoryRecordDedupeSeconds"/>) so gallery UI + hooks
        /// do not double-count one user action.
        /// </summary>
        internal static void TryRecordItemUseFromPath(string path, string kind)
        {
            if (ShouldSkipHistoryPath(path)) return;
            string key = BuildUsageKeyFromPath(path);
            if (string.IsNullOrEmpty(key)) return;
            TryRecordItemUse(key, kind ?? "");
        }

        /// <summary>Returns true when this key+kind was recorded very recently (caller should skip).</summary>
        private static bool IsRecentHistoryDuplicate(string itemKey, string kind)
        {
            if (string.IsNullOrEmpty(itemKey)) return true;
            try
            {
                DateTime now = DateTime.UtcNow;
                if (string.Equals(_lastHistoryRecordKey, itemKey, StringComparison.Ordinal)
                    && string.Equals(_lastHistoryRecordKind, kind ?? "", StringComparison.Ordinal)
                    && (now - _lastHistoryRecordUtc).TotalSeconds < HistoryRecordDedupeSeconds)
                {
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void RememberHistoryRecord(string itemKey, string kind)
        {
            try
            {
                _lastHistoryRecordKey = itemKey ?? "";
                _lastHistoryRecordKind = kind ?? "";
                _lastHistoryRecordUtc = DateTime.UtcNow;
            }
            catch { }
        }
    }
}
