using System;
using System.Collections.Generic;

namespace VPB.Api
{
    /// <summary>
    /// Stable public surface for companion plugins (e.g. the random-look HUD).
    /// <para>
    /// This exists so a companion DLL never reflects into VPB internals. Everything below is
    /// deliberately narrow and additive: <see cref="ApiVersion"/> is bumped on any breaking change,
    /// so a companion can refuse to run against an incompatible VPB rather than failing obscurely.
    /// </para>
    /// <para>
    /// Nothing here throws. Query calls return empty arrays and actions return false with a reason,
    /// because the caller is UI code that must stay responsive when the gallery is mid-refresh.
    /// </para>
    /// </summary>
    public static class VpbCompanion
    {
        /// <summary>Bumped on any breaking change to this class. Companions should check it at startup.</summary>
        public const int ApiVersion = 1;

        /// <summary>Resource types this API accepts, in the order the sidebar shows them.</summary>
        public static readonly string[] ResourceTypeNames =
            { "Appearance", "Clothing", "Hair", "Skin", "Morphs" };

        /// <summary>True when a gallery panel exists and the package index is queryable.</summary>
        public static bool IsReady
        {
            get
            {
                try
                {
                    return Gallery.singleton != null
                        && Gallery.singleton.Panels != null
                        && Gallery.singleton.Panels.Count > 0;
                }
                catch { return false; }
            }
        }

        /// <summary>Plugin version string, for display in a companion's title bar.</summary>
        public static string VpbVersion
        {
            get
            {
                try { return PluginVersionInfo.Version; }
                catch { return "?"; }
            }
        }

        /// <summary>
        /// Creators that have at least one scene, sorted by scene count descending then name.
        /// Feeds the companion's typable dropdown. Empty array when the index is not ready.
        /// </summary>
        public static string[] GetSceneCreators()
        {
            try
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (!VpbLocalDatabase.TryReadCreatorFileCounts(counts, "json", null, "Saves/scene", null, "Scenes", null))
                    return new string[0];

                var list = new List<KeyValuePair<string, int>>(counts.Count);
                foreach (var kv in counts)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                    list.Add(kv);
                }
                list.Sort((a, b) =>
                {
                    int byCount = b.Value.CompareTo(a.Value);
                    if (byCount != 0) return byCount;
                    return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
                });

                var outNames = new string[list.Count];
                for (int i = 0; i < list.Count; i++) outNames[i] = list[i].Key;
                return outNames;
            }
            catch (Exception ex)
            {
                LogUtil.LogWarning("[VPB.Api] GetSceneCreators failed: " + ex.Message);
                return new string[0];
            }
        }

        /// <summary>Uids of female Person atoms in the live scene — the valid import targets.</summary>
        public static string[] GetFemaleTargetUids()
        {
            try { return GalleryPanel.CompanionGetFemalePersonUids().ToArray(); }
            catch { return new string[0]; }
        }

        /// <summary>
        /// Roll a random scene by <paramref name="creator"/> (null/empty = any), take a random female
        /// person from it, and import <paramref name="resourceTypes"/> onto <paramref name="targetUid"/>.
        /// <para>
        /// Returns true once the run has been <i>started</i> — the import itself is asynchronous, since
        /// it waits on scene parse and on-demand package registration. A false return means the run
        /// could not begin and <paramref name="error"/> says why.
        /// </para>
        /// When the target uid is gone, the first live female is used instead rather than failing.
        /// Types the chosen source has nothing for are skipped by the existing import path.
        /// </summary>
        public static bool TryRandomFemaleImport(
            string creator,
            string targetUid,
            string[] resourceTypes,
            out string error)
        {
            error = null;
            try
            {
                GalleryPanel panel = ResolvePanel();
                if (panel == null)
                {
                    error = "No VPB gallery panel is available.";
                    return false;
                }

                var types = new List<VpbResourceType>(5);
                if (resourceTypes != null)
                {
                    for (int i = 0; i < resourceTypes.Length; i++)
                    {
                        VpbResourceType t;
                        if (TryParseResourceType(resourceTypes[i], out t)) types.Add(t);
                    }
                }
                if (types.Count == 0)
                {
                    error = "No valid resource types given (expected any of: "
                        + string.Join(", ", ResourceTypeNames) + ").";
                    return false;
                }

                return panel.CompanionRandomFemaleImport(creator, targetUid, types, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Maps an API type name onto VPB's internal enum. Unknown names are rejected, not guessed.</summary>
        static bool TryParseResourceType(string name, out VpbResourceType type)
        {
            type = VpbResourceType.Appearance;
            if (string.IsNullOrEmpty(name)) return false;
            if (string.Equals(name, "Appearance", StringComparison.OrdinalIgnoreCase)) { type = VpbResourceType.Appearance; return true; }
            if (string.Equals(name, "Clothing", StringComparison.OrdinalIgnoreCase)) { type = VpbResourceType.Clothing; return true; }
            if (string.Equals(name, "Hair", StringComparison.OrdinalIgnoreCase)) { type = VpbResourceType.Hair; return true; }
            if (string.Equals(name, "Skin", StringComparison.OrdinalIgnoreCase)) { type = VpbResourceType.Skin; return true; }
            if (string.Equals(name, "Morphs", StringComparison.OrdinalIgnoreCase)) { type = VpbResourceType.Morphs; return true; }
            return false;
        }

        /// <summary>Prefer a visible panel so the user sees what happened; otherwise any panel will do.</summary>
        static GalleryPanel ResolvePanel()
        {
            try
            {
                var g = Gallery.singleton;
                if (g == null || g.Panels == null || g.Panels.Count == 0) return null;
                for (int i = 0; i < g.Panels.Count; i++)
                {
                    GalleryPanel p = g.Panels[i];
                    if (p != null && p.IsVisible) return p;
                }
                for (int i = 0; i < g.Panels.Count; i++)
                {
                    GalleryPanel p = g.Panels[i];
                    if (p != null) return p;
                }
            }
            catch { }
            return null;
        }
    }
}
