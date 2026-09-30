using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VPB
{
    /// <summary>
    /// Panel-side half of the companion API (see <see cref="VPB.Api.VpbCompanion"/>).
    /// <para>
    /// Lives as a partial of <see cref="GalleryPanel"/> deliberately: the import sidebar's state is
    /// private instance state, and an external DLL reaching it by reflection would break on every
    /// rename. Everything here drives the existing, tested import path — <c>LoadSourceScene</c> →
    /// <c>WaitForImportSourceSceneReady</c> → <c>OnImportSidebarApplyClicked</c> — rather than
    /// duplicating it, so on-demand dependency prewarm and the clothing/hair catalog rebuild apply
    /// to companion-driven imports exactly as they do to a click.
    /// </para>
    /// </summary>
    public partial class GalleryPanel
    {
        /// <summary>Set (or clear, with null/empty) the creator filter without touching chips/UI state the user owns.</summary>
        internal bool CompanionSetCreatorFilter(string creator)
        {
            try
            {
                EnsureCurrentCreatorSet();
                _currentCreatorSet.Clear();
                if (!string.IsNullOrEmpty(creator))
                    _currentCreatorSet.Add(creator);
                SetCreatorFilterFromSetAndSync();
                return true;
            }
            catch (Exception ex)
            {
                LogUtil.LogWarning("[VPB.Api] CompanionSetCreatorFilter failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Current creator filter string ("" when unfiltered), for restoring after a companion run.</summary>
        internal string CompanionGetCreatorFilter()
        {
            try { return currentCreator ?? ""; }
            catch { return ""; }
        }

        /// <summary>Ensure the panel is showing Scenes; the random pool is the panel's filtered file list.</summary>
        internal bool CompanionEnsureScenesCategory()
        {
            try { return TryNavigateGalleryToScenes(); }
            catch { return false; }
        }

        /// <summary>Live female Person atoms, newest-last, as uids.</summary>
        internal static List<string> CompanionGetFemalePersonUids()
        {
            var outUids = new List<string>(8);
            try
            {
                var sc = SuperController.singleton;
                if (sc == null) return outUids;
                foreach (Atom a in sc.GetAtoms())
                {
                    if (a == null || a.type != "Person") continue;
                    if (!CompanionAtomIsFemale(a)) continue;
                    outUids.Add(a.uid);
                }
            }
            catch { }
            return outUids;
        }

        /// <summary>
        /// Female test for a live Person. Reads the character selector's own gender state rather than
        /// guessing from the uid, so a renamed atom still classifies correctly.
        /// </summary>
        internal static bool CompanionAtomIsFemale(Atom a)
        {
            if (a == null) return false;
            try
            {
                var selector = a.GetStorableByID("geometry") as DAZCharacterSelector;
                if (selector != null) return selector.gender == DAZCharacterSelector.Gender.Female;
            }
            catch { }
            // Selector unavailable (atom still initialising): fall back to not-male rather than
            // excluding it, so a just-spawned Person is still offered as a target.
            return true;
        }

        /// <summary>Resolve a target atom by uid, falling back to the first live female when it is gone.</summary>
        internal static Atom CompanionResolveTargetAtom(string preferredUid, out bool usedFallback)
        {
            usedFallback = false;
            var sc = SuperController.singleton;
            if (sc == null) return null;

            if (!string.IsNullOrEmpty(preferredUid))
            {
                try
                {
                    Atom exact = sc.GetAtomByUid(preferredUid);
                    if (exact != null && exact.type == "Person" && CompanionAtomIsFemale(exact)) return exact;
                }
                catch { }
            }

            List<string> females = CompanionGetFemalePersonUids();
            if (females.Count == 0) return null;
            usedFallback = true;
            try { return sc.GetAtomByUid(females[0]); }
            catch { return null; }
        }

        /// <summary>
        /// Drive one companion random import. Sets the creator filter, target atom and resource-type
        /// selection, then runs the same routine the sidebar's Random Scene button runs.
        /// </summary>
        internal bool CompanionRandomFemaleImport(
            string creator,
            string targetUid,
            ICollection<VpbResourceType> types,
            out string error)
        {
            error = null;
            try
            {
                if (types == null || types.Count == 0)
                {
                    error = "No resource types selected.";
                    return false;
                }

                bool usedFallback;
                Atom target = CompanionResolveTargetAtom(targetUid, out usedFallback);
                if (target == null)
                {
                    error = "No female Person atom in the live scene to import onto.";
                    return false;
                }

                if (!CompanionEnsureScenesCategory())
                {
                    error = "Could not switch the gallery to Scenes (source may be locked).";
                    return false;
                }

                CompanionSetCreatorFilter(creator);

                importSidebarTargetAtom = target;
                importSidebarMultiSelectedTypes.Clear();
                foreach (VpbResourceType t in types) importSidebarMultiSelectedTypes.Add(t);

                // Female-only content: the source person picked from the random scene must be female,
                // and the existing sidebar gender facets keep item rolls on the female side.
                _companionFemaleSourceOnly = true;

                StartCoroutine(CompanionRandomImportRoutine(creator, usedFallback));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                LogUtil.LogWarning("[VPB.Api] CompanionRandomFemaleImport failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Set while a companion run is in flight so the source-person pick stays female-only.</summary>
        private bool _companionFemaleSourceOnly;

        private IEnumerator CompanionRandomImportRoutine(string creator, bool usedFallbackTarget)
        {
            // The filtered pool is rebuilt asynchronously after a creator change. Waiting for
            // "pool is non-empty" is NOT a completion signal — the previous creator's results are
            // still in currentFilteredFiles, so that condition was already true on the first frame
            // and every creator change rolled from the OLD creator, taking effect one press late.
            //
            // galleryFileRefreshSequence increments at refresh START and refreshCoroutine is null
            // once it finishes, so "sequence advanced AND coroutine finished" is the real signal.
            yield return null;
            int seqBefore = GalleryFileRefreshSequence;
            RefreshFilesAndTabs();

            float deadline = Time.unscaledTime + 20f;
            bool started = false;
            bool settled = false;
            while (Time.unscaledTime < deadline)
            {
                if (!started && GalleryFileRefreshSequence != seqBefore) started = true;
                if (started && refreshCoroutine == null) { settled = true; break; }
                yield return null;
            }
            if (!settled)
            {
                LogUtil.LogWarning("[VPB.Api] Random import: gallery refresh did not settle in 20s"
                    + (started ? "" : " (refresh never started)") + "; rolling from the current pool.");
            }

            var pool = (currentFilteredFiles != null && currentFilteredFiles.Count > 0)
                ? currentFilteredFiles : lastFilteredFiles;
            if (pool == null || pool.Count == 0)
            {
                LogUtil.LogWarning("[VPB.Api] Random import: no scenes for creator '"
                    + (string.IsNullOrEmpty(creator) ? "(any)" : creator) + "'.");
                _companionFemaleSourceOnly = false;
                yield break;
            }

            // Second guard: prove the pool really is this creator's before rolling from it. Cheap,
            // and it turns a silent wrong-creator import into a visible warning.
            if (!string.IsNullOrEmpty(creator) && !CompanionPoolMatchesCreator(pool, creator))
            {
                LogUtil.LogWarning("[VPB.Api] Random import: pool still holds other creators after refresh"
                    + " (wanted '" + creator + "'); filtering the pick to '" + creator + "'.");
            }

            // Roll only from entries that actually belong to the requested creator. With "(any)" this
            // is the whole pool; with a creator it makes a stale pool impossible to mis-roll from.
            List<FileEntry> candidates = CompanionFilterPoolByCreator(pool, creator);
            if (candidates.Count == 0)
            {
                LogUtil.LogWarning("[VPB.Api] Random import: no scenes by creator '"
                    + (string.IsNullOrEmpty(creator) ? "(any)" : creator) + "' in a pool of " + pool.Count + ".");
                _companionFemaleSourceOnly = false;
                yield break;
            }

            // A scene may contain no female at all; re-roll rather than failing the press.
            const int MaxSceneAttempts = 8;
            for (int attempt = 1; attempt <= MaxSceneAttempts; attempt++)
            {
                FileEntry pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                if (pick == null) continue;

                LoadSourceScene(pick);
                yield return WaitForImportSourceSceneReady(30f);

                if (importSidebarSourcePersonIds.Count == 0)
                {
                    LogUtil.Log("[VPB.Api] Random import: '" + pick.Name + "' has no Person atoms; re-rolling ("
                        + attempt + "/" + MaxSceneAttempts + ").");
                    continue;
                }

                string femaleId = CompanionPickRandomFemaleSourceId();
                if (string.IsNullOrEmpty(femaleId))
                {
                    LogUtil.Log("[VPB.Api] Random import: '" + pick.Name + "' has no female Person; re-rolling ("
                        + attempt + "/" + MaxSceneAttempts + ").");
                    continue;
                }

                importSidebarSourceAtomId = femaleId;
                try { RenderSourceList(); } catch { }

                LogUtil.Log("[VPB.Api] Random import: creator='" + (string.IsNullOrEmpty(creator) ? "(any)" : creator)
                    + "' scene='" + pick.Name + "' source='" + femaleId + "' pool=" + pool.Count
                    + " attempt=" + attempt
                    + (usedFallbackTarget ? " (target fell back to first live female)" : ""));

                OnImportSidebarApplyClicked();
                _companionFemaleSourceOnly = false;
                yield break;
            }

            LogUtil.LogWarning("[VPB.Api] Random import: no female source found in " + MaxSceneAttempts
                + " scenes for creator '" + (string.IsNullOrEmpty(creator) ? "(any)" : creator) + "'.");
            _companionFemaleSourceOnly = false;
        }

        /// <summary>Creator half of a package uid ("Shapers.SIN4.1:/…" → "Shapers"), or "" for loose files.</summary>
        private static string CompanionCreatorOf(FileEntry f)
        {
            if (f == null) return "";
            try
            {
                string uid = f.Uid;
                if (string.IsNullOrEmpty(uid)) return "";
                int colon = uid.IndexOf(":/", StringComparison.Ordinal);
                string pkg = colon > 0 ? uid.Substring(0, colon) : uid;
                int dot = pkg.IndexOf('.');
                return dot > 0 ? pkg.Substring(0, dot) : "";
            }
            catch { return ""; }
        }

        /// <summary>Entries by <paramref name="creator"/>; the whole pool when creator is empty ("any").</summary>
        private static List<FileEntry> CompanionFilterPoolByCreator(List<FileEntry> pool, string creator)
        {
            var outList = new List<FileEntry>(pool != null ? pool.Count : 0);
            if (pool == null) return outList;
            bool any = string.IsNullOrEmpty(creator);
            for (int i = 0; i < pool.Count; i++)
            {
                FileEntry f = pool[i];
                if (f == null) continue;
                if (any || string.Equals(CompanionCreatorOf(f), creator, StringComparison.OrdinalIgnoreCase))
                    outList.Add(f);
            }
            return outList;
        }

        /// <summary>True when every sampled pool entry belongs to <paramref name="creator"/>.</summary>
        private static bool CompanionPoolMatchesCreator(List<FileEntry> pool, string creator)
        {
            if (pool == null || pool.Count == 0 || string.IsNullOrEmpty(creator)) return true;
            int sample = Math.Min(pool.Count, 12);
            for (int i = 0; i < sample; i++)
            {
                if (!string.Equals(CompanionCreatorOf(pool[i]), creator, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// A random female among the loaded source scene's Person atoms, or null when none qualify.
        /// Unknown-gender persons count as female only when the scene has no positively-female atom,
        /// so a scene VPB cannot classify still yields something rather than being skipped.
        /// </summary>
        private string CompanionPickRandomFemaleSourceId()
        {
            var females = new List<string>(4);
            var unknowns = new List<string>(4);
            try
            {
                SimpleJSON.JSONClass scene = EnsureLoadedSceneJSON();
                if (scene == null) scene = EnsureLoadedSceneJSONSync();

                for (int i = 0; i < importSidebarSourcePersonIds.Count; i++)
                {
                    string pid = importSidebarSourcePersonIds[i];
                    if (string.IsNullOrEmpty(pid)) continue;
                    int verdict = CompanionSceneAtomGender(scene, pid);
                    if (verdict > 0) females.Add(pid);
                    else if (verdict == 0) unknowns.Add(pid);
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogWarning("[VPB.Api] Female source pick failed: " + ex.Message);
            }

            if (females.Count > 0) return females[UnityEngine.Random.Range(0, females.Count)];
            if (unknowns.Count > 0) return unknowns[UnityEngine.Random.Range(0, unknowns.Count)];
            return null;
        }

        /// <summary>
        /// Gender of a Person atom inside a scene JSON: 1 female, -1 male, 0 unknown.
        /// Reads the <c>geometry</c> storable's <c>character</c> ("Female 1" / "Male 1"), which is what
        /// VaM writes, rather than guessing from the atom id — scenes routinely name atoms "Female"
        /// and "Male" regardless of the actual character loaded.
        /// </summary>
        private static int CompanionSceneAtomGender(SimpleJSON.JSONClass scene, string atomId)
        {
            if (scene == null || string.IsNullOrEmpty(atomId)) return 0;
            try
            {
                SimpleJSON.JSONArray atoms = scene["atoms"] != null ? scene["atoms"].AsArray : null;
                if (atoms == null) return 0;

                for (int i = 0; i < atoms.Count; i++)
                {
                    SimpleJSON.JSONNode node = atoms[i];
                    if (node == null) continue;
                    string id = node["id"] != null ? node["id"].Value : null;
                    if (!string.Equals(id, atomId, StringComparison.Ordinal)) continue;

                    SimpleJSON.JSONArray storables = node["storables"] != null ? node["storables"].AsArray : null;
                    if (storables == null) return 0;

                    for (int s = 0; s < storables.Count; s++)
                    {
                        SimpleJSON.JSONNode st = storables[s];
                        if (st == null) continue;
                        string sid = st["id"] != null ? st["id"].Value : null;
                        if (!string.Equals(sid, "geometry", StringComparison.OrdinalIgnoreCase)) continue;

                        string character = st["character"] != null ? st["character"].Value : null;
                        if (string.IsNullOrEmpty(character)) return 0;
                        if (character.StartsWith("Female", StringComparison.OrdinalIgnoreCase)) return 1;
                        if (character.StartsWith("Male", StringComparison.OrdinalIgnoreCase)) return -1;
                        // Custom characters (e.g. "Ashley") carry no gender word; the useMale flag does.
                        SimpleJSON.JSONNode useMale = st["useMaleMorphsOnFemale"];
                        if (useMale != null) return string.Equals(useMale.Value, "true", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
                        return 0;
                    }
                    return 0;
                }
            }
            catch { }
            return 0;
        }
    }
}
