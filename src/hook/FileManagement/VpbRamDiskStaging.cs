using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VPB
{
    /// <summary>
    /// Optional RAM-disk tier for on-demand package registration, cooperating with the external
    /// <c>RamDiskAuto</c> plugin (<c>BepInEx/plugins/RamDiskAuto.dll</c> + <c>RamDiskAutoWorker.exe</c>,
    /// data under <c>BepInEx/RamDiskAuto</c>).
    ///
    /// <para><b>Division of ownership — deliberate.</b> RamDiskAuto stages only <c>*.var</c>: its
    /// <c>safe_package_relative()</c> rejects any other suffix, so every <c>.DISABLED</c> archive is
    /// invisible to it. It moves the real file to the RAM disk and leaves a <i>symlink</i> at the
    /// AddonPackages path, tracked in <c>moved-files-journal.json</c> with SHA-256 verified restore.
    /// VPB must never stage a <c>.var</c> itself — two owners moving the same file, one of them
    /// without the journal, is how packages get lost. VPB stages only <c>.DISABLED</c> archives,
    /// which RamDiskAuto will not touch, into its own subfolder, and only ever <i>copies</i>:
    /// the original archive is never moved, renamed or deleted.</para>
    ///
    /// <para><b>Why a copy and not a hard link.</b> <see cref="VamOnDemandLoader"/> normally hard-links
    /// <c>Cache/VPB/ondemand/&lt;uid&gt;.var</c> onto the <c>.DISABLED</c> archive so VaM's
    /// 3-segment UID parser accepts it. A hard link cannot cross volumes, so it can never reach the
    /// RAM disk. Copying to <c>&lt;RamRoot&gt;/VPB/ondemand/&lt;uid&gt;.var</c> satisfies the same
    /// naming requirement and puts the bytes VaM will re-read in RAM.</para>
    ///
    /// <para>Every failure here is non-fatal: the caller falls back to the hard link, so behaviour
    /// with no RAM disk present is byte-for-byte what it was before.</para>
    /// </summary>
    internal static class VpbRamDiskStaging
    {
        /// <summary>VPB's own subtree under the RAM root. Never <c>RecentVARs</c>, which RamDiskAuto owns.</summary>
        private const string VpbRamSubdirectory = "VPB/ondemand";

        /// <summary>RamDiskAuto's BepInEx config, read for its <c>RamRoot</c> so both tools agree on the location.</summary>
        private const string RamDiskAutoConfigPath = "BepInEx/config/trey.ramdisk.auto.cfg";

        /// <summary>Written by RamDiskAuto.dll once it has verified the disk inside VaM.</summary>
        private const string RamDiskConfirmedMarker = "bepinex-confirmed.txt";

        private static readonly object s_Lock = new object();

        private static bool s_Probed;
        private static string s_RamStageDirectory;
        private static long s_StagedBytes;
        private static int s_StagedCount;
        private static int s_FailuresLogged;

        /// <summary>uid → staged path, so a second request for the same package reuses the RAM copy.</summary>
        private static readonly Dictionary<string, string> s_StagedByUid =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Insertion order of <see cref="s_StagedByUid"/> keys, oldest first — the LRU prune order.</summary>
        private static readonly List<string> s_StageOrder = new List<string>();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceExW(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailableToCaller,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        internal static bool IsEnabled
        {
            get
            {
                try
                {
                    var s = Settings.Instance;
                    return s != null && s.RamDiskStagingEnabled != null && s.RamDiskStagingEnabled.Value;
                }
                catch { return false; }
            }
        }

        private static long MaxStageBytes
        {
            get
            {
                try
                {
                    var s = Settings.Instance;
                    int mb = (s != null && s.RamDiskStagingBudgetMB != null) ? s.RamDiskStagingBudgetMB.Value : 768;
                    if (mb < 0) mb = 0;
                    if (mb > 65536) mb = 65536;
                    return (long)mb * 1024L * 1024L;
                }
                catch { return 768L * 1024L * 1024L; }
            }
        }

        /// <summary>
        /// Free bytes VPB refuses to consume. Defaults above RamDiskAuto's own 1 GiB
        /// <c>MIN_FREE_BYTES</c> so the primary owner's staging never starves because of VPB.
        /// </summary>
        private static long MinFreeBytes
        {
            get
            {
                try
                {
                    var s = Settings.Instance;
                    int mb = (s != null && s.RamDiskStagingMinFreeMB != null) ? s.RamDiskStagingMinFreeMB.Value : 1536;
                    if (mb < 256) mb = 256;
                    if (mb > 262144) mb = 262144;
                    return (long)mb * 1024L * 1024L;
                }
                catch { return 1536L * 1024L * 1024L; }
            }
        }

        /// <summary>
        /// Resolves the RAM stage directory once per session, creating it if the disk is present.
        /// Returns null when there is no usable RAM disk, which is the normal case on most installs.
        /// </summary>
        private static string GetRamStageDirectory()
        {
            lock (s_Lock)
            {
                if (s_Probed) return s_RamStageDirectory;
                s_Probed = true;
                s_RamStageDirectory = null;

                try
                {
                    if (!IsEnabled) return null;

                    string root = ResolveRamRoot();
                    if (string.IsNullOrEmpty(root)) return null;
                    if (!Directory.Exists(root))
                    {
                        LogUtil.Log("[VPB RamDisk] RamRoot '" + root + "' not present; using local hard links.");
                        return null;
                    }

                    string stage = Path.Combine(root, VpbRamSubdirectory.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(stage);

                    // Leftovers from a crash or a Windows restart: the RAM disk may survive a VaM
                    // exit, and a stale <uid>.var whose archive has since changed would register the
                    // wrong bytes. Start every session from an empty stage.
                    int removed = ClearDirectoryFiles(stage);

                    s_RamStageDirectory = stage;
                    LogUtil.Log("[VPB RamDisk] Staging .DISABLED packages at '" + stage
                        + "' (budget=" + FormatMB(MaxStageBytes) + " reserve=" + FormatMB(MinFreeBytes)
                        + " free=" + FormatMB(GetFreeBytes(stage))
                        + (removed > 0 ? " cleared_stale=" + removed : "") + ")");
                    return s_RamStageDirectory;
                }
                catch (Exception ex)
                {
                    LogUtil.LogWarning("[VPB RamDisk] Probe failed, using local hard links: " + ex.Message);
                    s_RamStageDirectory = null;
                    return null;
                }
            }
        }

        /// <summary>
        /// RamDiskAuto's <c>RamRoot</c> from its own config file, so a user who moved the disk only has
        /// to change it in one place. Falls back to the plugin's documented default.
        /// </summary>
        private static string ResolveRamRoot()
        {
            string configured = null;
            try
            {
                var s = Settings.Instance;
                if (s != null && s.RamDiskStagingRootOverride != null)
                {
                    string ov = (s.RamDiskStagingRootOverride.Value ?? "").Trim();
                    if (ov.Length > 0) return ov;
                }
            }
            catch { }

            try
            {
                if (File.Exists(RamDiskAutoConfigPath))
                {
                    foreach (string raw in File.ReadAllLines(RamDiskAutoConfigPath))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        if (!string.Equals(line.Substring(0, eq).Trim(), "RamRoot", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string val = line.Substring(eq + 1).Trim();
                        if (val.Length > 0) { configured = val; break; }
                    }
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogWarning("[VPB RamDisk] Could not read " + RamDiskAutoConfigPath + ": " + ex.Message);
            }

            if (!string.IsNullOrEmpty(configured)) return configured;
            return @"Z:\VaM-RamDisk";
        }

        /// <summary>
        /// Copies a <c>.DISABLED</c> archive into the RAM stage under a VaM-parsable
        /// <c>&lt;uid&gt;.var</c> name. Returns false whenever the caller should fall back to the
        /// on-disk hard link — no RAM disk, over budget, disk too full, or any I/O failure.
        /// </summary>
        internal static bool TryStageDisabledArchive(string archivePath, string uid, out string stagedPath)
        {
            stagedPath = null;
            if (string.IsNullOrEmpty(archivePath) || string.IsNullOrEmpty(uid)) return false;

            string stageDir = GetRamStageDirectory();
            if (string.IsNullOrEmpty(stageDir)) return false;

            lock (s_Lock)
            {
                try
                {
                    string existing;
                    if (s_StagedByUid.TryGetValue(uid, out existing))
                    {
                        if (!string.IsNullOrEmpty(existing) && File.Exists(existing))
                        {
                            stagedPath = existing;
                            return true;
                        }
                        Forget(uid);
                    }

                    var src = new FileInfo(archivePath);
                    if (!src.Exists) return false;
                    long size = src.Length;
                    if (size <= 0) return false;

                    string target = Path.Combine(stageDir, uid + ".var");

                    if (!EnsureRoomFor(stageDir, size, target))
                        return false;

                    // Copy through a temp name then rename: a torn copy must never be registrable.
                    string temp = target + ".vpbtmp";
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    File.Copy(archivePath, temp, true);

                    var copied = new FileInfo(temp);
                    if (!copied.Exists || copied.Length != size)
                    {
                        try { File.Delete(temp); } catch { }
                        LogUtil.LogWarning("[VPB RamDisk] Short copy for " + uid + "; falling back to hard link.");
                        return false;
                    }

                    try { if (File.Exists(target)) File.Delete(target); } catch { }
                    File.Move(temp, target);

                    s_StagedByUid[uid] = target;
                    s_StageOrder.Add(uid);
                    s_StagedBytes += size;
                    s_StagedCount++;

                    stagedPath = target;
                    return true;
                }
                catch (Exception ex)
                {
                    if (++s_FailuresLogged <= 8)
                        LogUtil.LogWarning("[VPB RamDisk] Staging failed for " + uid + " (" + ex.Message + "); using hard link.");
                    return false;
                }
            }
        }

        /// <summary>
        /// Makes room for <paramref name="size"/> by evicting least-recently-staged packages, honouring
        /// both VPB's own budget and the free-space reserve left for RamDiskAuto. Caller holds the lock.
        /// </summary>
        private static bool EnsureRoomFor(string stageDir, long size, string targetPath)
        {
            long budget = MaxStageBytes;
            if (budget <= 0) return false;
            if (size > budget) return false;

            long reserve = MinFreeBytes;

            for (int guard = 0; guard < 4096; guard++)
            {
                long free = GetFreeBytes(stageDir);
                bool overBudget = s_StagedBytes + size > budget;
                bool overFree = free >= 0 && free - size < reserve;
                if (!overBudget && !overFree) return true;
                if (!EvictOldest(targetPath)) return false;
            }
            return false;
        }

        /// <summary>Drops the oldest staged copy. Never evicts the package currently being staged.</summary>
        private static bool EvictOldest(string protectedTargetPath)
        {
            while (s_StageOrder.Count > 0)
            {
                string uid = s_StageOrder[0];
                s_StageOrder.RemoveAt(0);

                string path;
                if (!s_StagedByUid.TryGetValue(uid, out path)) continue;
                if (!string.IsNullOrEmpty(protectedTargetPath)
                    && string.Equals(path, protectedTargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Keep it addressable but move it to the back rather than deleting our own target.
                    s_StageOrder.Add(uid);
                    return false;
                }

                long freed = 0;
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists) { freed = fi.Length; fi.Delete(); }
                }
                catch (Exception ex)
                {
                    // A package VaM still has open cannot be deleted; stop rather than spin.
                    if (++s_FailuresLogged <= 8)
                        LogUtil.LogWarning("[VPB RamDisk] Could not evict " + uid + ": " + ex.Message);
                    s_StagedByUid.Remove(uid);
                    return false;
                }

                s_StagedByUid.Remove(uid);
                s_StagedBytes -= freed;
                if (s_StagedBytes < 0) s_StagedBytes = 0;
                s_StagedCount--;
                if (s_StagedCount < 0) s_StagedCount = 0;
                return true;
            }
            return false;
        }

        private static void Forget(string uid)
        {
            string path;
            if (s_StagedByUid.TryGetValue(uid, out path))
            {
                long size = 0;
                try { var fi = new FileInfo(path); if (fi.Exists) size = fi.Length; } catch { }
                s_StagedBytes -= size;
                if (s_StagedBytes < 0) s_StagedBytes = 0;
                s_StagedCount--;
                if (s_StagedCount < 0) s_StagedCount = 0;
            }
            s_StagedByUid.Remove(uid);
            s_StageOrder.Remove(uid);
        }

        private static int ClearDirectoryFiles(string dir)
        {
            int removed = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    try { File.Delete(f); removed++; } catch { }
                }
            }
            catch { }
            return removed;
        }

        private static long GetFreeBytes(string path)
        {
            try
            {
                ulong avail, total, free;
                string dir = path;
                if (!dir.EndsWith("\\", StringComparison.Ordinal) && !dir.EndsWith("/", StringComparison.Ordinal))
                    dir += Path.DirectorySeparatorChar;
                if (GetDiskFreeSpaceExW(dir, out avail, out total, out free))
                    return (long)Math.Min(avail, long.MaxValue);
            }
            catch { }
            return -1;
        }

        private static string FormatMB(long bytes)
        {
            if (bytes < 0) return "?";
            return (bytes / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture) + "MB";
        }

        /// <summary>One-line summary for the on-demand startup checkpoint.</summary>
        internal static string DescribeState()
        {
            lock (s_Lock)
            {
                if (string.IsNullOrEmpty(s_RamStageDirectory))
                    return s_Probed ? "ramdisk=off" : "ramdisk=unprobed";
                var sb = new StringBuilder(64);
                sb.Append("ramdisk=on staged=").Append(s_StagedCount)
                  .Append(' ').Append(FormatMB(s_StagedBytes))
                  .Append("/").Append(FormatMB(MaxStageBytes))
                  .Append(" free=").Append(FormatMB(GetFreeBytes(s_RamStageDirectory)));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Removes every RAM copy VPB made. Called on shutdown so the disk is not left holding
        /// gigabytes VaM no longer needs; safe to call when no RAM disk was ever used.
        /// </summary>
        internal static void ReleaseAll()
        {
            lock (s_Lock)
            {
                if (string.IsNullOrEmpty(s_RamStageDirectory)) return;
                int removed = ClearDirectoryFiles(s_RamStageDirectory);
                s_StagedByUid.Clear();
                s_StageOrder.Clear();
                s_StagedBytes = 0;
                s_StagedCount = 0;
                if (removed > 0)
                    LogUtil.Log("[VPB RamDisk] Released " + removed + " staged package(s) on shutdown.");
            }
        }
    }
}
