using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VPB
{
    public static class ImageLoaderThreadedHook
    {
        private static string s_VamRootWithSep;
        private static readonly object s_TagLock = new object();

        // Tag is the source descriptor (package UID, load dir, or "loose") captured at enqueue time on
        // the main thread. Queue->Process is async, so reading CurrentLoadDir at Process time races —
        // load dir is often already popped. Dictionary is bounded by QueuedImagePool size; each
        // re-enqueue overwrites, so no explicit cleanup needed.
        private static readonly Dictionary<ImageLoaderThreaded.QueuedImage, string> s_LoadContextSource =
            new Dictionary<ImageLoaderThreaded.QueuedImage, string>();

        public static void PatchAll(Harmony harmony)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                s_VamRootWithSep = root + Path.DirectorySeparatorChar;

                Type qiType = typeof(ImageLoaderThreaded.QueuedImage);
                Type iltType = typeof(ImageLoaderThreaded);
                Type[] sig = new Type[] { qiType };

                MethodInfo processMethod = AccessTools.Method(qiType, "Process");
                if (processMethod == null)
                {
                    LogUtil.LogError("ImageLoaderThreadedHook: QueuedImage.Process not found, path guard NOT active");
                    return;
                }
                harmony.Patch(processMethod, prefix: new HarmonyMethod(typeof(ImageLoaderThreadedHook), nameof(ProcessPrefix)));

                HarmonyMethod tagPrefix = new HarmonyMethod(typeof(ImageLoaderThreadedHook), nameof(TagQueuePrefix));
                string[] queueMethods = { "QueueImage", "QueueThumbnail", "QueueThumbnailImmediate", "ProcessImageImmediate" };
                foreach (string name in queueMethods)
                {
                    MethodInfo m = AccessTools.Method(iltType, name, sig);
                    if (m == null)
                    {
                        LogUtil.LogWarning("ImageLoaderThreadedHook: ImageLoaderThreaded." + name + " not found, scene-load tag for that path disabled");
                        continue;
                    }
                    harmony.Patch(m, prefix: tagPrefix);
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogError("ImageLoaderThreadedHook PatchAll failed: " + ex);
            }
        }

        public static void TagQueuePrefix(ImageLoaderThreaded.QueuedImage qi)
        {
            if (qi == null) return;

            string loadDir = FileManager.CurrentLoadDir;
            bool isLoading = SuperController.singleton != null && SuperController.singleton.isLoading;
            bool fromLoad = loadDir != null || isLoading;

            string source = null;
            if (fromLoad)
            {
                string pkgUid = null;
                try { pkgUid = FileManager.CurrentPackageUid; } catch { }
                source = pkgUid ?? loadDir ?? "loose";
            }

            lock (s_TagLock)
            {
                if (fromLoad) s_LoadContextSource[qi] = source;
                else s_LoadContextSource.Remove(qi);
            }
        }

        public static bool ProcessPrefix(ImageLoaderThreaded.QueuedImage __instance)
        {
            if (__instance == null) return true;

            string source;
            bool fromLoad;
            lock (s_TagLock)
            {
                fromLoad = s_LoadContextSource.TryGetValue(__instance, out source);
            }
            if (!fromLoad) return true;

            string path = __instance.imgPath;
            string reason;
            if (IsPathSafe(path, out reason)) return true;

            LogUtil.LogWarning("[VPB] Blocked texture load from scene/preset source=\"" + (source ?? "<unknown>") + "\" path=\"" + path + "\" reason=" + reason);
            __instance.processed = true;
            __instance.hadError = true;
            __instance.errorText = "Blocked by VPB path guard: " + reason;
            return false;
        }

        private static bool IsPathSafe(string path, out string reason)
        {
            reason = null;

            if (string.IsNullOrEmpty(path) || path == "NULL") return true;

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return true;

            if (path.StartsWith("\\\\") || path.StartsWith("//"))
            {
                reason = "UNC path";
                return false;
            }

            // Drive-letter absolute (C:/foo) and VFS prefix (SELF:/foo, Author.Name.1:/foo) both contain ":/".
            // Discriminate by colon position: drive letter is always one char.
            bool isDriveLetter = path.Length >= 2 && path[1] == ':';
            if (!isDriveLetter && path.IndexOf(":/", StringComparison.Ordinal) > 0)
                return true;

            string anchored;
            try
            {
                anchored = (isDriveLetter || Path.IsPathRooted(path))
                    ? path
                    : Path.Combine(s_VamRootWithSep, path);
            }
            catch (Exception ex)
            {
                reason = "Path anchoring failed: " + ex.Message;
                return false;
            }

            string full;
            try
            {
                full = Path.GetFullPath(anchored);
            }
            catch (Exception ex)
            {
                reason = "Path resolution failed: " + ex.Message;
                return false;
            }

            if (!full.StartsWith(s_VamRootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Outside VaM root, resolved: " + full;
                return false;
            }

            return true;
        }
    }
}
