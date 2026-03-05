// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║                     ASSET DUPLICATE RESOLVER                               ║
// ║                                                                              ║
// ║  Created by  AL!Z (Aliz Pasztor)                                            ║
// ║  Portfolio   https://alizpasztor.com/                                        ║
// ║                                                                              ║
// ║  If this tool saved you time on a real project, I'd love to hear about it.  ║
// ║  I build tools, plugins, and audio systems that other engineers find too     ║
// ║  complex or too weird. Game audio · DAW plugins · Live visualization.       ║
// ║                                                                              ║
// ║  Free to use in personal and commercial projects.                            ║
// ║  Please do not redistribute or resell.                                       ║
// ╚══════════════════════════════════════════════════════════════════════════════╝
//
// WHAT THIS TOOL DOES
//   Scans one or two folders and categorises assets into:
//     • Duplicates   — same asset exists in both folders; swap references to the source copy
//     • Unique Used  — only in the duplicate folder but actively referenced; migrate safely
//     • Unused       — not referenced anywhere; send to bin for review before deletion
//
//   Single-folder mode (leave Folder 2 empty): audits one folder for used vs. unused assets.
//
// FEATURES
//   • Content-based duplicate detection (MD5 hash) — catches same-content, different-name assets
//   • GUID collision detection — finds corrupted .meta files sharing a GUID
//   • Orphaned .meta file detection — finds .meta files with no matching asset
//   • Non-blocking chunked scan with live progress bar
//   • Write-time-invalidating dependency AND hash cache — only rescans what changed
//   • Property-level reference swapping with full selective undo
//   • Auto-swap dry-run preview — shows exact file/property count before committing
//   • Asset diff window — side-by-side metadata, MD5 identity, pixel similarity %, waveform
//   • Dependency graph with configurable depth (1-10 levels)
//   • Scene cross-reference with click-through to GameObjects in any scene (opens additively)
//   • Hover asset preview panel
//   • Size threshold filter — hide assets below a configurable file size
//   • Ignore list (folder-based, persistent)
//   • Export scan report to CSV
//   • Persistent settings via EditorPrefs
//   • VCS checkout support (Perforce / Plastic SCM)
//
// REQUIREMENTS
//   Unity 2022.2+   No external packages required.
//
// INSTALL
//   Drop this file inside any Editor folder in your project.
//   Open via  Tools > Asset Duplicate Resolver
//

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.VersionControl;
using UnityEngine;

namespace ProjectTools.EditorOnly
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  Brand palette — AL!Z / alizpasztor.com
    // ─────────────────────────────────────────────────────────────────────────────

    internal static class Pal
    {
        // ── Brand ────────────────────────────────────────────────────────────────
        public static readonly Color Orange = new Color(1.000f, 0.369f, 0.000f); // #FF5E00
        public static readonly Color Pink = new Color(1.000f, 0.180f, 0.533f); // #FF2E88
        public static readonly Color Cyan = new Color(0.000f, 0.898f, 1.000f); // #00E5FF
        public static readonly Color ElectricBlue = new Color(0.420f, 0.310f, 1.000f); // #6B4FFF

        // ── Backgrounds ──────────────────────────────────────────────────────────
        public static readonly Color BgBase = new Color(0.039f, 0.039f, 0.059f); // #0A0A0F
        public static readonly Color BgPanel = new Color(0.082f, 0.082f, 0.125f); // #151520
        public static readonly Color BgSection = new Color(0.106f, 0.067f, 0.122f); // #1B111F
        public static readonly Color BgDeepPurple = new Color(0.176f, 0.106f, 0.306f); // #2D1B4E
        public static readonly Color BgDark = new Color(0.102f, 0.043f, 0.180f); // #1A0B2E

        // ── Text ─────────────────────────────────────────────────────────────────
        public static readonly Color Text = new Color(0.910f, 0.910f, 0.941f); // #E8E8F0
        public static readonly Color TextDim = new Color(0.600f, 0.580f, 0.650f); // mid tone

        // ── Borders ──────────────────────────────────────────────────────────────
        public static readonly Color Border = new Color(0.165f, 0.165f, 0.208f); // #2A2A35

        // ── Section accents (left-edge bars) ─────────────────────────────────────
        public static readonly Color AccentDup = Orange;                             // Duplicates
        public static readonly Color AccentUniq = Cyan;                               // Unique Used
        public static readonly Color AccentUnused = Pink;                               // Unused
        public static readonly Color AccentScene = new Color(0.180f, 0.820f, 0.700f); // teal
        public static readonly Color AccentGuid = ElectricBlue;                       // GUID collisions
        public static readonly Color AccentOrphan = new Color(0.700f, 0.300f, 1.000f); // violet
        public static readonly Color AccentBin = Pink;
        public static readonly Color AccentHistory = ElectricBlue;
        public static readonly Color AccentIgnore = new Color(0.700f, 0.600f, 0.300f); // amber

        // ── Gradient helper — fake a left-to-right two-stop gradient with two rects ──
        public static void DrawGradientBar(Rect r, Color left, Color right)
        {
            // Unity IMGUI can't do real gradients, so we approximate with ~16 vertical slices
            int slices = 16;
            float sliceW = r.width / slices;
            for (int i = 0; i < slices; i++)
            {
                float t = (float)i / (slices - 1);
                Color col = Color.Lerp(left, right, t);
                col.a = 1f;
                EditorGUI.DrawRect(new Rect(r.x + i * sliceW, r.y, sliceW + 1f, r.height), col);
            }
        }

        // ── Scanline overlay — thin horizontal lines for a CRT / cyberpunk feel ──
        public static void DrawScanlines(Rect r, float alpha = 0.08f)
        {
            var lineColor = new Color(0f, 0f, 0f, alpha);
            for (float y = r.y; y < r.yMax; y += 3f)
                EditorGUI.DrawRect(new Rect(r.x, y, r.width, 1f), lineColor);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Dependency cache — session-scoped, write-time-invalidated
    // ─────────────────────────────────────────────────────────────────────────────

    public static class DependencyCache
    {
        private struct Entry
        {
            public long tick;
            public string[] deps;
        }

        private static readonly Dictionary<string, Entry> s_cache = new Dictionary<string, Entry>(2048);

        public static string[] Get(string path, bool recursive)
        {
            string key = path + (recursive ? ":r" : ":i");
            long tick = GetTick(path);

            if (s_cache.TryGetValue(key, out var entry) && entry.tick == tick)
                return entry.deps;

            var deps = AssetDatabase.GetDependencies(path, recursive);
            s_cache[key] = new Entry { tick = tick, deps = deps };
            return deps;
        }

        public static void Invalidate(string path)
        {
            s_cache.Remove(path + ":r");
            s_cache.Remove(path + ":i");
        }

        public static void InvalidateAll()
        {
            s_cache.Clear();
        }

        public static int CacheCount => s_cache.Count;

        private static long GetTick(string path)
        {
            try { return File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return 0L; }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  File hash cache — MD5, write-time-invalidated
    // ─────────────────────────────────────────────────────────────────────────────

    public static class FileHashCache
    {
        private struct Entry
        {
            public long tick;
            public string hash;       // raw bytes hash
            public string normHash;   // YAML-normalised hash (name-stripped)
        }

        private static readonly Dictionary<string, Entry> s_cache = new Dictionary<string, Entry>(1024);

        // Raw MD5 — for binary assets (textures, audio, etc.)
        public static string Get(string path)
        {
            long tick = GetTick(path);

            if (s_cache.TryGetValue(path, out var entry) && entry.tick == tick)
                return entry.hash;

            var e = BuildEntry(path, tick);
            s_cache[path] = e;
            return e.hash;
        }

        // Normalised hash — strips m_Name from Unity YAML so renamed duplicates still match.
        // Falls back to raw hash for binary assets.
        public static string GetNormalised(string path)
        {
            long tick = GetTick(path);

            if (s_cache.TryGetValue(path, out var entry) && entry.tick == tick)
                return entry.normHash;

            var e = BuildEntry(path, tick);
            s_cache[path] = e;
            return e.normHash;
        }

        public static void Invalidate(string path)
        {
            s_cache.Remove(path);
        }

        public static int CacheCount => s_cache.Count;

        // These extensions use Unity's YAML text serialisation and embed m_Name inside the file.
        // Duplicating any of them via the Editor changes m_Name to match the new filename,
        // so raw MD5 will differ even when the content is otherwise identical.
        private static readonly HashSet<string> s_yamlExts = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ".mat", ".prefab", ".asset", ".anim", ".controller",
            ".playable", ".overrideController", ".mask", ".physicMaterial",
            ".physicsMaterial2D", ".flare", ".renderTexture", ".cubemap",
            ".guiskin", ".fontsettings", ".shadervariants"
        };

        private static Entry BuildEntry(string path, long tick)
        {
            string raw = ComputeRawMD5(path);
            string ext = Path.GetExtension(path);
            string norm = s_yamlExts.Contains(ext) ? ComputeNormalisedMD5(path) : raw;

            return new Entry { tick = tick, hash = raw, normHash = norm };
        }

        // Standard MD5 over raw file bytes
        private static string ComputeRawMD5(string path)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(path))
                {
                    byte[] bytes = md5.ComputeHash(stream);
                    return System.BitConverter.ToString(bytes).Replace("-", "").ToLower();
                }
            }
            catch
            {
                return "";
            }
        }

        // Reads the YAML line by line, replaces the value of m_Name with a fixed
        // placeholder, then hashes the result.  Everything else — shader GUIDs,
        // property values, sub-asset structure — is preserved, so the hash still
        // correctly distinguishes materials with different properties.
        private static string ComputeNormalisedMD5(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path);

                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();

                    // Match "  m_Name: AnyValue" — leading whitespace varies by nesting depth
                    if (trimmed.StartsWith("m_Name:"))
                    {
                        // Replace just the value portion so indentation is preserved
                        int colon = lines[i].IndexOf("m_Name:");
                        lines[i] = lines[i].Substring(0, colon) + "m_Name: __ADR_NORMALISED__";
                    }
                }

                string joined = string.Join("\n", lines);

                using (var md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(joined));
                    return System.BitConverter.ToString(bytes).Replace("-", "").ToLower();
                }
            }
            catch
            {
                // If anything goes wrong fall back to raw hash so we don't lose the result
                return ComputeRawMD5(path);
            }
        }

        private static long GetTick(string path)
        {
            try { return File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return 0L; }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Postprocessor — invalidates both caches on any asset change
    // ─────────────────────────────────────────────────────────────────────────────

    public class AssetCacheInvalidator : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] imported,
            string[] deleted,
            string[] movedTo,
            string[] movedFrom)
        {
            foreach (var p in imported) { DependencyCache.Invalidate(p); FileHashCache.Invalidate(p); }
            foreach (var p in deleted) { DependencyCache.Invalidate(p); FileHashCache.Invalidate(p); }
            foreach (var p in movedTo) { DependencyCache.Invalidate(p); FileHashCache.Invalidate(p); }
            foreach (var p in movedFrom) { DependencyCache.Invalidate(p); FileHashCache.Invalidate(p); }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Main window
    // ─────────────────────────────────────────────────────────────────────────────

    public class AssetDuplicateResolver : EditorWindow
    {
        // ── EditorPrefs keys ─────────────────────────────────────────────────────

        private const string K_F1 = "ADR_f1";
        private const string K_F2 = "ADR_f2";
        private const string K_IGN = "ADR_ign";
        private const string K_DEPTH = "ADR_dep";
        private const string K_SWIMM = "ADR_swi";
        private const string K_HASH = "ADR_hash";
        private const string K_FD = "ADR_fd";
        private const string K_FUQ = "ADR_fuq";
        private const string K_FUU = "ADR_fuu";
        private const string K_FSU = "ADR_fsu";
        private const string K_FSNU = "ADR_fsnu";
        private const string K_SZSZ = "ADR_szsz";
        private const string K_SAVE = "ADR_sav";

        // ── Folders ──────────────────────────────────────────────────────────────

        private string folder1 = "";
        private string folder2 = "";

        // ── Ignore list ──────────────────────────────────────────────────────────

        private List<string> ignoreFolders = new List<string>();
        private bool showIgnore;
        private Vector2 ignoreScroll;

        // ── Asset maps ───────────────────────────────────────────────────────────

        private Dictionary<string, string> f1ByRel = new Dictionary<string, string>();
        private Dictionary<string, string> f2ByRel = new Dictionary<string, string>();
        private Dictionary<string, string> f1ByHash = new Dictionary<string, string>();
        private Dictionary<string, string> contentMirror = new Dictionary<string, string>(); // f2 path → f1 mirror

        // ── GUID collisions ───────────────────────────────────────────────────────

        private Dictionary<string, List<string>> guidCollisions = new Dictionary<string, List<string>>();
        private bool showGuidCollisions;
        private Vector2 guidCollisionScroll;

        // ── Orphaned .meta files ─────────────────────────────────────────────────

        private List<string> orphanedMetas = new List<string>();
        private bool showOrphans;
        private Vector2 orphanScroll;

        // ── Two-folder result lists ───────────────────────────────────────────────

        private List<string> duplicates = new List<string>();
        private List<string> uniqueUsed = new List<string>();
        private List<string> unused = new List<string>();
        private List<string> noGuid = new List<string>();

        // ── Single-folder result lists ────────────────────────────────────────────

        private List<string> sfUsed = new List<string>();
        private List<string> sfUnused = new List<string>();

        // ── Size totals (bytes) ───────────────────────────────────────────────────

        private long szDup, szUnq, szUnu, szSfU, szSfN;

        // ── Bin ──────────────────────────────────────────────────────────────────

        private List<string> bin = new List<string>();
        private Dictionary<string, BinSource> binSource = new Dictionary<string, BinSource>();

        // ── Selection maps ────────────────────────────────────────────────────────

        private Dictionary<string, bool> selDup = new Dictionary<string, bool>();
        private Dictionary<string, bool> selUnq = new Dictionary<string, bool>();
        private Dictionary<string, bool> selUnu = new Dictionary<string, bool>();
        private Dictionary<string, bool> selSfU = new Dictionary<string, bool>();
        private Dictionary<string, bool> selSfN = new Dictionary<string, bool>();

        // ── Reference cache ───────────────────────────────────────────────────────

        private Dictionary<string, List<string>> refCache = new Dictionary<string, List<string>>();

        // ── Scene cross-reference ─────────────────────────────────────────────────

        private Dictionary<string, List<string>> sceneCrossRef = new Dictionary<string, List<string>>();
        private bool showSceneRef;
        private Vector2 sceneRefScroll;

        // ── Undo history ──────────────────────────────────────────────────────────

        private List<ActionRecord> history = new List<ActionRecord>();
        private bool showHistory;
        private Vector2 histScroll;

        // ── Scan state ────────────────────────────────────────────────────────────

        private IEnumerator _scanEnum;
        private bool _scanning;
        private float _progress;
        private string _phase = "";

        // ── Settings ──────────────────────────────────────────────────────────────

        private int depDepth = 3;
        private bool swapImmOnly = false;
        private bool useHashDetect = true;
        private bool autoSave = true;
        private long sizeThreshold = 0;     // bytes — 0 means show all

        // ── Hover preview ─────────────────────────────────────────────────────────

        private string _hoverAsset = "";
        private Texture _hoverTex;
        private double _hoverTime;
        private const double HOVER_DELAY = 0.4;

        // ── UI state ─────────────────────────────────────────────────────────────

        private Vector2 scroll;
        private string filter = "";
        private string status = "";
        private bool showBin = true;
        private bool showNoGuid;
        private bool fDup = true;
        private bool fUnq = true;
        private bool fUnu = true;
        private bool fSU = true;
        private bool fSNU = true;

        // ── External change detection ─────────────────────────────────────────────

        private double lastScan;
        private double lastRepaint;
        private HashSet<string> known = new HashSet<string>();
        private const double REPAINT_INTERVAL = 0.3;

        // ── Cached styles ─────────────────────────────────────────────────────────

        private GUIStyle _hdr;
        private GUIStyle _sec;
        private GUIStyle _path;
        private GUIStyle _dim;
        private bool _stylesOk;

        // ── Convenience ───────────────────────────────────────────────────────────

        private bool Single => string.IsNullOrWhiteSpace(folder2);

        // ─────────────────────────────────────────────────────────────────────────
        //  Menu item
        // ─────────────────────────────────────────────────────────────────────────

        [MenuItem("Tools/Asset Duplicate Resolver")]
        public static void Open()
        {
            var w = GetWindow<AssetDuplicateResolver>("Asset Duplicate Resolver");
            w.minSize = new Vector2(560, 440);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            folder1 = EditorPrefs.GetString(K_F1, "");
            folder2 = EditorPrefs.GetString(K_F2, "");
            depDepth = EditorPrefs.GetInt(K_DEPTH, 3);
            swapImmOnly = EditorPrefs.GetBool(K_SWIMM, false);
            useHashDetect = EditorPrefs.GetBool(K_HASH, true);
            fDup = EditorPrefs.GetBool(K_FD, true);
            fUnq = EditorPrefs.GetBool(K_FUQ, true);
            fUnu = EditorPrefs.GetBool(K_FUU, true);
            fSU = EditorPrefs.GetBool(K_FSU, true);
            fSNU = EditorPrefs.GetBool(K_FSNU, true);
            sizeThreshold = (long)EditorPrefs.GetFloat(K_SZSZ, 0f);
            autoSave = EditorPrefs.GetBool(K_SAVE, true);

            string raw = EditorPrefs.GetString(K_IGN, "");
            ignoreFolders = string.IsNullOrEmpty(raw)
                ? new List<string>()
                : raw.Split('|').Where(s => !string.IsNullOrEmpty(s)).ToList();
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(K_F1, folder1);
            EditorPrefs.SetString(K_F2, folder2);
            EditorPrefs.SetInt(K_DEPTH, depDepth);
            EditorPrefs.SetBool(K_SWIMM, swapImmOnly);
            EditorPrefs.SetBool(K_HASH, useHashDetect);
            EditorPrefs.SetBool(K_FD, fDup);
            EditorPrefs.SetBool(K_FUQ, fUnq);
            EditorPrefs.SetBool(K_FUU, fUnu);
            EditorPrefs.SetBool(K_FSU, fSU);
            EditorPrefs.SetBool(K_FSNU, fSNU);
            EditorPrefs.SetFloat(K_SZSZ, sizeThreshold);
            EditorPrefs.SetBool(K_SAVE, autoSave);
            EditorPrefs.SetString(K_IGN, string.Join("|", ignoreFolders));
        }

        private void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            // Advance the scan coroutine one step per update
            if (_scanEnum != null)
            {
                if (!_scanEnum.MoveNext())
                    _scanEnum = null;

                Repaint();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - lastRepaint > REPAINT_INTERVAL)
            {
                if (!_scanning && lastScan > 0 && HasExternalChange())
                    status = "⚠ Assets changed externally — re-scan recommended.";

                lastRepaint = now;
                Repaint();
            }
        }

        private bool HasExternalChange()
        {
            foreach (var p in known)
            {
                if (!File.Exists(p) && !AssetDatabase.IsValidFolder(p))
                    return true;
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  OnGUI
        // ─────────────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isPlaying) return;
            if (!HasOpenInstances<AssetDuplicateResolver>()) return;

            InitStyles();
            DrawHeader();

            GUILayout.Space(4);
            EditorGUI.BeginDisabledGroup(_scanning);
            folder1 = FolderField("Folder:", folder1);
            folder2 = FolderField("Duplicate Folder (leave empty for single-folder mode):", folder2);
            EditorGUI.EndDisabledGroup();

            DrawIgnoreList();
            DrawToolbar();
            DrawFilterBar();
            DrawStatusBar();

            EditorGUI.BeginDisabledGroup(_scanning);
            scroll = EditorGUILayout.BeginScrollView(scroll);

            if (Single)
            {
                DrawSection(
                    ref fSU,
                    "Used Assets — referenced somewhere in project",
                    sfUsed, selSfU, szSfU,
                    Pal.AccentUniq,
                    actions: null,
                    diffBtn: false);

                DrawSection(
                    ref fSNU,
                    "Unused Assets — not referenced anywhere",
                    sfUnused, selSfN, szSfN,
                    Pal.AccentUnused,
                    actions: () =>
                    {
                        if (GUILayout.Button("Send Selected to Bin"))
                            BinList(sfUnused, selSfN, BinSource.SfUnused);
                    },
                    diffBtn: false);
            }
            else
            {
                DrawSection(
                    ref fDup,
                    $"Duplicates — swap references to {ShortName(folder1)}",
                    duplicates, selDup, szDup,
                    Pal.AccentDup,
                    actions: () =>
                    {
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button("Swap Selected References")) ConfirmSwap();
                        if (GUILayout.Button("Swap All References", GUILayout.Width(130))) ConfirmSwapAll();
                        if (GUILayout.Button("Bin Selected", GUILayout.Width(88))) BinSelectedDuplicates();
                        EditorGUILayout.EndHorizontal();
                    },
                    diffBtn: true);

                DrawSection(
                    ref fUnq,
                    $"Unique Used — migrate into {ShortName(folder1)}",
                    uniqueUsed, selUnq, szUnq,
                    Pal.AccentUniq,
                    actions: () =>
                    {
                        if (GUILayout.Button("Move Selected to MigratedFromFolder2"))
                            MoveUnique();
                    },
                    diffBtn: false);

                DrawSection(
                    ref fUnu,
                    "Unused Assets — safe to remove",
                    unused, selUnu, szUnu,
                    Pal.AccentUnused,
                    actions: () =>
                    {
                        if (GUILayout.Button("Send Selected to Bin"))
                            BinList(unused, selUnu, BinSource.Unused);
                    },
                    diffBtn: false);
            }

            DrawSceneCrossRef();
            DrawGuidCollisions();
            DrawOrphanedMetas();

            if (noGuid.Count > 0)
            {
                GUILayout.Space(8);
                Rect ngBar = EditorGUILayout.GetControlRect(false, 22);
                EditorGUI.DrawRect(ngBar, Pal.BgSection);
                EditorGUI.DrawRect(new Rect(ngBar.x, ngBar.y, 4, ngBar.height), Pal.Border);
                showNoGuid = EditorGUI.Foldout(
                    new Rect(ngBar.x + 8, ngBar.y + 3, ngBar.width, 16),
                    showNoGuid,
                    $"  Skipped — No GUID ({noGuid.Count})",
                    true);

                if (showNoGuid)
                {
                    var ngStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Pal.TextDim } };
                    foreach (var a in noGuid)
                        GUILayout.Label("  " + a, ngStyle);
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUI.EndDisabledGroup();

            DrawBinSection();
            DrawUndoSection();
            DrawHoverPreview();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Header
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 44);

            // Background: deep dark base
            EditorGUI.DrawRect(r, Pal.BgDark);

            // Orange→Pink gradient strip along the bottom edge
            Pal.DrawGradientBar(new Rect(r.x, r.yMax - 2, r.width, 2), Pal.Orange, Pal.Pink);

            // Subtle scanline overlay for CRT feel
            Pal.DrawScanlines(r, 0.06f);

            // Left accent bar
            EditorGUI.DrawRect(new Rect(r.x, r.y, 3, r.height), Pal.Orange);

            // Title
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Pal.Text }
            };
            GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 180, 20), "Asset Duplicate Resolver", titleStyle);

            // Subtitle / tagline
            var subStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = Pal.Orange }
            };
            GUI.Label(new Rect(r.x + 12, r.y + 26, r.width - 180, 14), "by AL!Z  •  alizpasztor.com", subStyle);

            // Right-aligned cache info
            var infoStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = Pal.ElectricBlue },
                alignment = TextAnchor.MiddleRight
            };
            GUI.Label(new Rect(r.x, r.y + 6, r.width - 8, 14), $"dep cache: {DependencyCache.CacheCount}", infoStyle);
            GUI.Label(new Rect(r.x, r.y + 22, r.width - 8, 14), $"hash cache: {FileHashCache.CacheCount}", infoStyle);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Toolbar (scan button, toggles, depth)
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawToolbar()
        {
            GUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();

            if (_scanning)
            {
                if (GUILayout.Button("✕  Cancel", GUILayout.Height(26)))
                    CancelScan();
            }
            else
            {
                if (GUILayout.Button("⟳  Scan Project", GUILayout.Height(26)))
                    StartScan();
            }

            EditorGUI.BeginDisabledGroup(_scanning);
            swapImmOnly = GUILayout.Toggle(swapImmOnly, " Immediate refs only", GUILayout.Width(148));
            useHashDetect = GUILayout.Toggle(useHashDetect, " Content match", GUILayout.Width(110));
            autoSave = GUILayout.Toggle(autoSave, " Auto-save", GUILayout.Width(86));
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(lastScan == 0 || _scanning);
            if (GUILayout.Button("Export Report", GUILayout.Width(110)))
                ExportReport();
            EditorGUI.EndDisabledGroup();

            GUILayout.Label("Dep depth:", GUILayout.Width(70));
            depDepth = EditorGUILayout.IntSlider(depDepth, 1, 10);

            EditorGUILayout.EndHorizontal();

            // Size threshold row
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label("Min size filter:", GUILayout.Width(90));

            long prevThreshold = sizeThreshold;
            float thresholdKB = sizeThreshold / 1024f;
            thresholdKB = EditorGUILayout.Slider(thresholdKB, 0f, 10240f);
            sizeThreshold = (long)(thresholdKB * 1024f);

            string threshLabel = sizeThreshold == 0 ? "Off" : FormatBytes(sizeThreshold);
            GUILayout.Label(threshLabel, _dim, GUILayout.Width(72));

            EditorGUILayout.EndHorizontal();

            // Progress bar
            if (_scanning)
            {
                Rect pr = EditorGUILayout.GetControlRect(false, 20);
                // Draw custom progress bar using brand colours
                EditorGUI.DrawRect(pr, Pal.BgPanel);
                EditorGUI.DrawRect(new Rect(pr.x, pr.y, pr.width, 1), Pal.Border);
                EditorGUI.DrawRect(new Rect(pr.x, pr.yMax - 1, pr.width, 1), Pal.Border);
                float fillW = Mathf.Max(4f, pr.width * _progress);
                Pal.DrawGradientBar(new Rect(pr.x, pr.y, fillW, pr.height), Pal.Orange, Pal.Pink);
                Pal.DrawScanlines(new Rect(pr.x, pr.y, fillW, pr.height), 0.10f);
                var progStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Pal.Text }
                };
                GUI.Label(pr, $"{_phase}  {Mathf.RoundToInt(_progress * 100)}%", progStyle);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Filter bar
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawFilterBar()
        {
            GUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();

            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = Pal.TextDim }
            };
            GUILayout.Label("Filter:", labelStyle, GUILayout.Width(42));
            filter = EditorGUILayout.TextField(filter);

            if (GUILayout.Button("✕", GUILayout.Width(24)))
            {
                filter = "";
                // Defocus the text field so it reflects the cleared value immediately
                GUIUtility.keyboardControl = 0;
                GUI.FocusControl(null);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Status bar
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawStatusBar()
        {
            if (string.IsNullOrEmpty(status)) return;

            bool isWarning = status.Contains("⚠") || status.Contains("error");
            bool isSuccess = status.Contains("complete") || status.Contains("exported") || status.Contains("Swap complete") || status.Contains("Moved");

            Rect r = EditorGUILayout.GetControlRect(false, 22);
            EditorGUI.DrawRect(r, Pal.BgPanel);

            // Left accent bar colour depends on message type
            Color barCol = isWarning ? Pal.Pink : isSuccess ? Pal.Cyan : Pal.ElectricBlue;
            EditorGUI.DrawRect(new Rect(r.x, r.y, 3, r.height), barCol);

            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = isWarning ? Pal.Pink : isSuccess ? Pal.Cyan : Pal.TextDim },
                wordWrap = true
            };

            GUI.Label(new Rect(r.x + 8, r.y + 3, r.width - 12, r.height - 4), status, style);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Ignore list
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawIgnoreList()
        {
            GUILayout.Space(2);

            Rect bar = EditorGUILayout.GetControlRect(false, 22);
            EditorGUI.DrawRect(bar, Pal.BgSection);
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, 3, bar.height), Pal.AccentIgnore);
            EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 1, bar.width, 1), Pal.Border);

            showIgnore = EditorGUI.Foldout(
                new Rect(bar.x + 6, bar.y + 3, bar.width, 16),
                showIgnore,
                $"  Ignore List ({ignoreFolders.Count})",
                true);

            if (!showIgnore) return;

            ignoreScroll = EditorGUILayout.BeginScrollView(ignoreScroll, GUILayout.MaxHeight(72));

            foreach (var folder in ignoreFolders.ToList())
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(folder, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                if (GUILayout.Button("✕", GUILayout.Width(22)))
                    ignoreFolders.Remove(folder);

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("+ Add Folder", GUILayout.Height(20)))
            {
                string abs = EditorUtility.OpenFolderPanel("Ignore Folder", Application.dataPath, "");

                if (!string.IsNullOrEmpty(abs))
                {
                    abs = abs.Replace("\\", "/");
                    string dp = Application.dataPath.Replace("\\", "/");

                    if (abs.StartsWith(dp))
                    {
                        string rel = "Assets" + abs.Substring(dp.Length);
                        if (!ignoreFolders.Contains(rel))
                            ignoreFolders.Add(rel);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Invalid Folder", "Select a folder inside Assets.", "OK");
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Category section
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawSection(
            ref bool fold,
            string title,
            List<string> assets,
            Dictionary<string, bool> sel,
            long totalBytes,
            Color accent,
            System.Action actions,
            bool diffBtn)
        {
            GUILayout.Space(6);

            Rect bar = EditorGUILayout.GetControlRect(false, 24);
            EditorGUI.DrawRect(bar, Pal.BgSection);

            // Gradient accent bar: solid accent left, fading to transparent right
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, 4, bar.height), accent);
            EditorGUI.DrawRect(new Rect(bar.x + 4, bar.y, 40, bar.height),
                new Color(accent.r, accent.g, accent.b, 0.12f));

            // Bottom border line
            EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 1, bar.width, 1), Pal.Border);

            fold = EditorGUI.Foldout(
                new Rect(bar.x + 8, bar.y + 4, bar.width - 110, 16),
                fold, "", true);

            // Show filtered size when a filter is active, otherwise show total
            bool filtering = !string.IsNullOrEmpty(filter) || sizeThreshold > 0;
            string sizeLabel = "";
            if (filtering)
            {
                long filteredBytes = assets
                    .Where(a => PassesFilter(a) && PassesThreshold(a))
                    .Sum(a => { try { return new FileInfo(a).Length; } catch { return 0L; } });
                sizeLabel = filteredBytes > 0 ? $"  {FormatBytes(filteredBytes)}" : "";
            }
            else if (totalBytes > 0)
            {
                sizeLabel = $"  {FormatBytes(totalBytes)}";
            }

            var secStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                normal = { textColor = Pal.Text }
            };

            GUI.Label(
                new Rect(bar.x + 20, bar.y + 4, bar.width - 120, 16),
                $"{title}  ({FilteredCount(assets)}{sizeLabel})",
                secStyle);

            if (assets.Count > 0)
            {
                var btnStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    normal = { textColor = Pal.TextDim },
                    hover = { textColor = Pal.Cyan }
                };

                if (GUI.Button(new Rect(bar.xMax - 100, bar.y + 3, 46, 18), "All", btnStyle))
                {
                    foreach (var a in assets)
                        sel[a] = PassesFilter(a);
                }

                if (GUI.Button(new Rect(bar.xMax - 50, bar.y + 3, 46, 18), "None", btnStyle))
                {
                    foreach (var a in assets)
                        sel[a] = false;
                }
            }

            if (!fold) return;

            if (assets.Count == 0)
            {
                GUILayout.Label("  Nothing here.", EditorStyles.miniLabel);
                return;
            }

            GUILayout.Space(2);
            actions?.Invoke();
            GUILayout.Space(4);

            foreach (var asset in assets)
            {
                if (!PassesFilter(asset)) continue;
                if (!PassesThreshold(asset)) continue;

                if (!sel.ContainsKey(asset))
                    sel[asset] = true;

                bool fileExists = File.Exists(asset);

                EditorGUILayout.BeginHorizontal();

                sel[asset] = EditorGUILayout.Toggle(sel[asset], GUILayout.Width(18));

                if (!fileExists)
                {
                    GUI.color = Color.yellow;
                    GUILayout.Label("⚠", GUILayout.Width(16));
                    GUI.color = Color.white;
                }
                else
                {
                    Texture icon = AssetDatabase.GetCachedIcon(asset);
                    if (icon != null)
                        GUILayout.Label(icon, GUILayout.Width(18), GUILayout.Height(18));
                    else
                        GUILayout.Space(18);
                }

                bool hasContentMatch = contentMirror.ContainsKey(asset);
                string badge = hasContentMatch ? " [≡]" : "";
                string displayName = asset.Replace(folder2 + "/", "").Replace(folder1 + "/", "");
                string tooltip = BuildRefTooltip(asset);

                GUI.color = fileExists ? Color.white : new Color(1f, 1f, 0.4f);

                Rect labelRect = EditorGUILayout.GetControlRect(GUILayout.ExpandWidth(true));
                GUI.Label(labelRect, new GUIContent($"{displayName}{badge}  {SizeString(asset)}", tooltip), _path);

                GUI.color = Color.white;

                // Hover detection
                if (Event.current.type == EventType.MouseMove)
                {
                    if (labelRect.Contains(Event.current.mousePosition))
                    {
                        if (_hoverAsset != asset)
                        {
                            _hoverAsset = asset;
                            _hoverTex = null;
                            _hoverTime = EditorApplication.timeSinceStartup;
                        }
                    }
                    else if (_hoverAsset == asset)
                    {
                        _hoverAsset = "";
                    }
                }

                if (GUILayout.Button("Ping", GUILayout.Width(44)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<Object>(asset);
                    if (obj) EditorGUIUtility.PingObject(obj);
                }

                if (refCache.TryGetValue(asset, out var refs) && refs.Count > 0)
                {
                    if (GUILayout.Button($"Refs ({refs.Count})", GUILayout.Width(72)))
                        ReferencePingWindow.Show(asset, refs);
                }
                else
                {
                    GUILayout.Space(76);
                }

                if (GUILayout.Button("Deps", GUILayout.Width(40)))
                    DependencyGraphWindow.Show(asset, depDepth);

                if (diffBtn)
                {
                    string mirror = GetMirror(asset);

                    if (mirror != null)
                    {
                        if (GUILayout.Button("Diff", GUILayout.Width(36)))
                            AssetDiffWindow.Show(asset, mirror);
                    }
                    else
                    {
                        GUILayout.Space(40);
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Scene cross-reference panel
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawSceneCrossRef()
        {
            if (sceneCrossRef.Count == 0) return;

            GUILayout.Space(6);

            Rect bar = EditorGUILayout.GetControlRect(false, 24);
            EditorGUI.DrawRect(bar, Pal.BgSection);
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, 4, bar.height), Pal.AccentScene);
            EditorGUI.DrawRect(new Rect(bar.x + 4, bar.y, 40, bar.height), new Color(Pal.AccentScene.r, Pal.AccentScene.g, Pal.AccentScene.b, 0.12f));
            EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 1, bar.width, 1), Pal.Border);

            showSceneRef = EditorGUI.Foldout(
                new Rect(bar.x + 8, bar.y + 4, bar.width, 16),
                showSceneRef,
                $"  Scene Cross-Reference ({sceneCrossRef.Count})",
                true);

            if (!showSceneRef) return;

            sceneRefScroll = EditorGUILayout.BeginScrollView(sceneRefScroll, GUILayout.MaxHeight(160));

            string activeScene = EditorSceneManager.GetActiveScene().path;

            foreach (var kvp in sceneCrossRef.OrderBy(k => k.Key))
            {
                string sceneName = Path.GetFileNameWithoutExtension(kvp.Key);
                bool isOpen = kvp.Key == activeScene;

                EditorGUILayout.BeginHorizontal();

                GUILayout.Label(
                    $"🎬 {sceneName}{(isOpen ? " (open)" : "")}",
                    EditorStyles.boldLabel,
                    GUILayout.ExpandWidth(true));

                GUILayout.Label($"{kvp.Value.Count}", _dim, GUILayout.Width(28));

                if (GUILayout.Button("Ping", GUILayout.Width(44)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<Object>(kvp.Key);
                    if (obj) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
                }

                if (GUILayout.Button("Find Objects", GUILayout.Width(88)))
                    FindInScene(kvp.Key, kvp.Value, activeScene);

                EditorGUILayout.EndHorizontal();

                foreach (var a in kvp.Value)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(16);

                    Texture icon = AssetDatabase.GetCachedIcon(a);
                    if (icon != null)
                        GUILayout.Label(icon, GUILayout.Width(16), GUILayout.Height(16));

                    GUILayout.Label(Path.GetFileName(a), EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                    EditorGUILayout.EndHorizontal();
                }

                GUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        private void FindInScene(string scenePath, List<string> targetAssets, string activeScene)
        {
            bool isOpen = scenePath == activeScene;

            if (!isOpen)
            {
                bool confirmed = EditorUtility.DisplayDialog(
                    "Open Scene Additively?",
                    $"'{Path.GetFileNameWithoutExtension(scenePath)}' is not open.\n\nOpen additively to search for referencing GameObjects?",
                    "Open Additively",
                    "Cancel");

                if (!confirmed) return;

                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }

            var targetSet = new HashSet<string>(targetAssets);
            var hits = new List<GameObject>();

            foreach (var comp in Object.FindObjectsByType<Component>(FindObjectsSortMode.None))
            {
                if (!comp) continue;

                var so = new SerializedObject(comp);
                var sp = so.GetIterator();

                while (sp.NextVisible(true))
                {
                    if (sp.propertyType == SerializedPropertyType.ObjectReference
                        && sp.objectReferenceValue
                        && targetSet.Contains(AssetDatabase.GetAssetPath(sp.objectReferenceValue)))
                    {
                        hits.Add(comp.gameObject);
                        break;
                    }
                }
            }

            if (hits.Count == 0)
            {
                Debug.Log($"[ADR] No GameObjects found in '{Path.GetFileNameWithoutExtension(scenePath)}'.");
            }
            else
            {
                Selection.objects = hits.Select(g => (Object)g).ToArray();
                EditorGUIUtility.PingObject(hits[0]);
                Debug.Log($"[ADR] Selected {hits.Count} GameObject(s) in '{Path.GetFileNameWithoutExtension(scenePath)}'.");
            }

            if (!isOpen)
            {
                bool closeIt = EditorUtility.DisplayDialog(
                    "Close Scene?",
                    $"Close '{Path.GetFileNameWithoutExtension(scenePath)}'?",
                    "Close",
                    "Keep Open");

                if (closeIt)
                {
                    var scene = EditorSceneManager.GetSceneByPath(scenePath);
                    if (scene.IsValid())
                        EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  GUID collision panel
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawGuidCollisions()
        {
            if (guidCollisions.Count == 0) return;

            GUILayout.Space(6);

            Rect bar = EditorGUILayout.GetControlRect(false, 24);
            EditorGUI.DrawRect(bar, Pal.BgSection);
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, 4, bar.height), Pal.AccentGuid);
            EditorGUI.DrawRect(new Rect(bar.x + 4, bar.y, 40, bar.height), new Color(Pal.AccentGuid.r, Pal.AccentGuid.g, Pal.AccentGuid.b, 0.12f));
            EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 1, bar.width, 1), Pal.Border);

            showGuidCollisions = EditorGUI.Foldout(
                new Rect(bar.x + 8, bar.y + 4, bar.width, 16),
                showGuidCollisions,
                $"  ⚠ GUID Collisions ({guidCollisions.Count})",
                true);

            if (!showGuidCollisions) return;

            GUILayout.Label(
                "These assets share a GUID — indicates .meta file corruption.\nFix: delete one asset's .meta and let Unity regenerate it.",
                EditorStyles.helpBox);

            guidCollisionScroll = EditorGUILayout.BeginScrollView(guidCollisionScroll, GUILayout.MaxHeight(120));

            foreach (var kvp in guidCollisions)
            {
                GUILayout.Label($"GUID: {kvp.Key}", EditorStyles.boldLabel);

                foreach (var path in kvp.Value)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(12);
                    GUILayout.Label(path, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                    if (GUILayout.Button("Ping", GUILayout.Width(44)))
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                        if (obj) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
                    }

                    EditorGUILayout.EndHorizontal();
                }

                GUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Orphaned .meta files panel
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawOrphanedMetas()
        {
            if (orphanedMetas.Count == 0) return;

            GUILayout.Space(6);

            Rect bar = EditorGUILayout.GetControlRect(false, 24);
            EditorGUI.DrawRect(bar, Pal.BgSection);
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, 4, bar.height), Pal.AccentOrphan);
            EditorGUI.DrawRect(new Rect(bar.x + 4, bar.y, 40, bar.height), new Color(Pal.AccentOrphan.r, Pal.AccentOrphan.g, Pal.AccentOrphan.b, 0.12f));
            EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 1, bar.width, 1), Pal.Border);

            showOrphans = EditorGUI.Foldout(
                new Rect(bar.x + 8, bar.y + 4, bar.width, 16),
                showOrphans,
                $"  Orphaned .meta Files ({orphanedMetas.Count})",
                true);

            if (!showOrphans) return;

            GUILayout.Label(
                "These .meta files have no matching asset — Unity left them behind after a delete or move outside the Editor.\nSafe to delete manually from disk.",
                EditorStyles.helpBox);

            orphanScroll = EditorGUILayout.BeginScrollView(orphanScroll, GUILayout.MaxHeight(120));

            foreach (var metaPath in orphanedMetas)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(metaPath, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                if (GUILayout.Button("Delete", GUILayout.Width(56)))
                {
                    bool confirmed = EditorUtility.DisplayDialog(
                        "Delete Orphaned .meta?",
                        $"Permanently delete:\n{metaPath}",
                        "Delete",
                        "Cancel");

                    if (confirmed)
                    {
                        File.Delete(metaPath);
                        orphanedMetas.Remove(metaPath);
                        break; // list modified, exit loop safely
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            GUILayout.Space(2);

            if (GUILayout.Button("Delete All Orphaned .meta Files", GUILayout.Height(20)))
            {
                bool confirmed = EditorUtility.DisplayDialog(
                    "Delete All Orphaned .meta Files?",
                    $"Permanently delete {orphanedMetas.Count} orphaned .meta file(s)?",
                    "Delete All",
                    "Cancel");

                if (confirmed)
                {
                    foreach (var metaPath in orphanedMetas.ToList())
                    {
                        try { File.Delete(metaPath); }
                        catch { Debug.LogWarning($"[ADR] Could not delete: {metaPath}"); }
                    }

                    orphanedMetas.Clear();
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Hover preview
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawHoverPreview()
        {
            if (string.IsNullOrEmpty(_hoverAsset)) return;
            if (EditorApplication.timeSinceStartup - _hoverTime < HOVER_DELAY) return;

            if (_hoverTex == null)
            {
                _hoverTex = AssetPreview.GetAssetPreview(AssetDatabase.LoadAssetAtPath<Object>(_hoverAsset))
                         ?? AssetDatabase.GetCachedIcon(_hoverAsset);
            }

            if (_hoverTex == null) return;

            const float SIZE = 96f;
            Vector2 mp = Event.current.mousePosition;

            Rect r = new Rect(
                Mathf.Clamp(mp.x + 14, 0, position.width - SIZE),
                Mathf.Clamp(mp.y - SIZE / 2f, 0, position.height - SIZE),
                SIZE, SIZE);

            EditorGUI.DrawRect(new Rect(r.x - 2, r.y - 2, SIZE + 4, SIZE + 4), new Color(0.1f, 0.1f, 0.12f));
            GUI.DrawTexture(r, _hoverTex, ScaleMode.ScaleToFit);
            Repaint();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Bin panel
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawBinSection()
        {
            if (bin.Count == 0) return;

            GUILayout.Space(6);

            Rect bar = EditorGUILayout.GetControlRect(false, 24);
            EditorGUI.DrawRect(bar, Pal.BgSection);
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, 4, bar.height), Pal.AccentBin);
            EditorGUI.DrawRect(new Rect(bar.x + 4, bar.y, 40, bar.height), new Color(Pal.AccentBin.r, Pal.AccentBin.g, Pal.AccentBin.b, 0.12f));
            EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 1, bar.width, 1), Pal.Border);

            showBin = EditorGUI.Foldout(
                new Rect(bar.x + 8, bar.y + 4, bar.width, 16),
                showBin,
                $"  Bin — {bin.Count} asset(s) pending deletion",
                true);

            if (!showBin) return;

            GUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("⚠ Empty Bin — Permanently Delete All"))
            {
                if (EditorUtility.DisplayDialog(
                    "Confirm Empty Bin",
                    $"Permanently delete {bin.Count} asset(s)? This cannot be undone.",
                    "Delete",
                    "Cancel"))
                {
                    EmptyBin();
                }
            }

            if (GUILayout.Button("Restore All", GUILayout.Width(84)))
                RestoreAllFromBin();

            EditorGUILayout.EndHorizontal();

            foreach (var a in bin.ToList())
            {
                bool stillExists = File.Exists(a);

                EditorGUILayout.BeginHorizontal();

                // Colour: cyan = still on disk (pending delete), dim = already gone externally
                var binItemStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = stillExists ? Pal.Cyan : Pal.TextDim }
                };

                if (!stillExists)
                    GUILayout.Label("✗", new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Pal.Pink } }, GUILayout.Width(14));
                else
                    GUILayout.Label("●", new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Pal.Orange } }, GUILayout.Width(14));

                GUILayout.Label(a, binItemStyle, GUILayout.ExpandWidth(true));

                if (stillExists && GUILayout.Button("Restore", GUILayout.Width(60)))
                    RestoreFromBin(a);

                if (!stillExists && GUILayout.Button("Clear", GUILayout.Width(60)))
                {
                    bin.Remove(a);
                    binSource.Remove(a);
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Undo / history panel
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawUndoSection()
        {
            if (history.Count == 0) return;

            GUILayout.Space(6);

            Rect bar = EditorGUILayout.GetControlRect(false, 24);
            EditorGUI.DrawRect(bar, Pal.BgSection);
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, 4, bar.height), Pal.AccentHistory);
            EditorGUI.DrawRect(new Rect(bar.x + 4, bar.y, 40, bar.height), new Color(Pal.AccentHistory.r, Pal.AccentHistory.g, Pal.AccentHistory.b, 0.12f));
            EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 1, bar.width, 1), Pal.Border);

            showHistory = EditorGUI.Foldout(
                new Rect(bar.x + 8, bar.y + 4, bar.width, 16),
                showHistory,
                $"  Action History ({history.Count})",
                true);

            if (!showHistory) return;

            histScroll = EditorGUILayout.BeginScrollView(histScroll, GUILayout.MaxHeight(120));

            for (int i = history.Count - 1; i >= 0; i--)
            {
                var rec = history[i];

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label($"[{i + 1}]  {rec.Summary}", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                if (rec.type != ActionType.Bin)
                {
                    if (GUILayout.Button("Undo", GUILayout.Width(50)))
                    {
                        ReverseAction(rec);
                        history.RemoveAt(i);
                        MaybeSave();
                        AssetDatabase.Refresh();
                        break;
                    }
                }
                else
                {
                    GUILayout.Label("→ use Bin", _dim, GUILayout.Width(60));
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Folder picker field
        // ─────────────────────────────────────────────────────────────────────────

        private string FolderField(string label, string path)
        {
            EditorGUILayout.BeginHorizontal();
            path = EditorGUILayout.TextField(label, path);

            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string abs = EditorUtility.OpenFolderPanel("Select Folder", Application.dataPath, "");

                if (!string.IsNullOrEmpty(abs))
                {
                    abs = abs.Replace("\\", "/");
                    string dp = Application.dataPath.Replace("\\", "/");

                    if (abs.StartsWith(dp))
                        path = "Assets" + abs.Substring(dp.Length);
                    else
                        EditorUtility.DisplayDialog("Invalid Folder", "Select a folder inside Assets.", "OK");
                }
            }

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                EditorGUILayout.HelpBox("Folder not found: " + path, MessageType.Warning);

            return path;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Scan
        // ─────────────────────────────────────────────────────────────────────────

        private void StartScan()
        {
            if (_scanning) return;

            // Validate folder 1 — always required
            if (string.IsNullOrWhiteSpace(folder1) || !AssetDatabase.IsValidFolder(folder1))
            {
                status = "⚠ Folder 1 is empty or does not exist. Fix the path before scanning.";
                return;
            }

            // Validate folder 2 — only required if something was typed in
            if (!string.IsNullOrWhiteSpace(folder2) && !AssetDatabase.IsValidFolder(folder2))
            {
                status = "⚠ Folder 2 path is invalid or does not exist. Fix the path or leave it empty for single-folder mode.";
                return;
            }

            _scanning = true;
            _progress = 0f;
            _scanEnum = ScanCoroutine();
        }

        private void CancelScan()
        {
            _scanEnum = null;
            _scanning = false;
            _progress = 0f;
            status = "Scan cancelled.";
        }

        private IEnumerator ScanCoroutine()
        {
            // ── Phase 1: Reset all state ─────────────────────────────────────────

            _phase = "Resetting";

            f1ByRel.Clear();
            f2ByRel.Clear();
            f1ByHash.Clear();
            contentMirror.Clear();
            duplicates.Clear();
            uniqueUsed.Clear();
            unused.Clear();
            sfUsed.Clear();
            sfUnused.Clear();
            noGuid.Clear();
            selDup.Clear();
            selUnq.Clear();
            selUnu.Clear();
            selSfU.Clear();
            selSfN.Clear();
            refCache.Clear();
            sceneCrossRef.Clear();
            guidCollisions.Clear();
            orphanedMetas.Clear();
            known.Clear();
            szDup = szUnq = szUnu = szSfU = szSfN = 0;
            // NOTE: bin and binSource are intentionally NOT cleared here.
            // Assets in the bin persist across rescans — they are excluded from
            // result lists below and highlighted in the Bin panel instead.

            yield return null;

            // ── Phase 2: Build folder maps, GUID collision detection, orphan scan ─

            _phase = "Building folder maps";
            _progress = 0.04f;

            string[] allPaths = AssetDatabase.GetAllAssetPaths();
            bool single = Single;
            var targets = new List<string>();
            var cands = new List<string>();
            var guidMap = new Dictionary<string, List<string>>();
            var metaSet = new HashSet<string>(); // all .meta paths that have a real asset

            foreach (var path in allPaths)
            {
                if (!path.StartsWith("Assets/")) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;
                if (IsIgnored(path)) continue;

                // Track this asset's .meta as having a real asset backing it
                metaSet.Add(path + ".meta");

                // GUID collision check
                string metaPath = path + ".meta";
                if (File.Exists(metaPath))
                {
                    string guid = ExtractGuid(metaPath);
                    if (!string.IsNullOrEmpty(guid))
                    {
                        if (!guidMap.ContainsKey(guid))
                            guidMap[guid] = new List<string>();

                        guidMap[guid].Add(path);
                    }
                }

                // Folder sorting
                if (single)
                {
                    if (path.StartsWith(folder1 + "/"))
                    {
                        f1ByRel[path.Substring(folder1.Length + 1)] = path;
                        targets.Add(path);
                    }
                    else
                    {
                        cands.Add(path);
                    }
                }
                else
                {
                    if (path.StartsWith(folder1 + "/"))
                    {
                        f1ByRel[path.Substring(folder1.Length + 1)] = path;
                    }
                    else if (path.StartsWith(folder2 + "/"))
                    {
                        f2ByRel[path.Substring(folder2.Length + 1)] = path;
                        targets.Add(path);
                    }
                    else
                    {
                        cands.Add(path);
                    }
                }
            }

            // Collect GUID collisions (guid shared by 2+ assets)
            foreach (var kvp in guidMap)
            {
                if (kvp.Value.Count > 1)
                    guidCollisions[kvp.Key] = kvp.Value;
            }

            // ── Phase 2b: Find orphaned .meta files ──────────────────────────────

            _phase = "Scanning for orphaned .meta files";
            _progress = 0.07f;

            string absDataPath = Application.dataPath.Replace("\\", "/");

            foreach (var metaFile in Directory.EnumerateFiles(absDataPath, "*.meta", SearchOption.AllDirectories))
            {
                string rel = "Assets" + metaFile.Replace("\\", "/").Substring(absDataPath.Length);

                if (!metaSet.Contains(rel))
                {
                    // The asset this .meta refers to does not exist in the AssetDatabase
                    string assetPath = rel.Substring(0, rel.Length - 5); // strip ".meta"
                    if (!File.Exists(assetPath) && !AssetDatabase.IsValidFolder(assetPath))
                        orphanedMetas.Add(rel);
                }
            }

            yield return null;

            // ── Phase 2c: Hash folder 1 for content matching (two-folder only) ───

            if (!single && useHashDetect)
            {
                _phase = "Hashing source folder";
                _progress = 0.10f;

                int hi = 0;
                foreach (var kvp in f1ByRel)
                {
                    // Use the normalised hash so that YAML assets (materials, prefabs, etc.)
                    // match even when the duplicate was renamed and Unity changed m_Name inside the file.
                    string h = FileHashCache.GetNormalised(kvp.Value);
                    if (!string.IsNullOrEmpty(h) && !f1ByHash.ContainsKey(h))
                        f1ByHash[h] = kvp.Value;

                    if (++hi % 50 == 0)
                    {
                        status = $"Hashing source folder… {hi}/{f1ByRel.Count}";
                        yield return null;
                    }
                }
            }

            // ── Phase 3: Dependency scan (chunked) ───────────────────────────────

            _phase = "Scanning dependencies";
            _progress = 0.12f;

            var usedSet = new HashSet<string>();
            var tgtSet = new HashSet<string>(targets);
            int total = cands.Count;
            const int CHUNK = 40;

            for (int i = 0; i < total; i++)
            {
                string ap = cands[i];
                _progress = 0.12f + 0.70f * ((float)i / Mathf.Max(total, 1));

                var deps = DependencyCache.Get(ap, true);
                var sceneHits = new List<string>();

                foreach (var dep in deps)
                {
                    if (dep != ap && tgtSet.Contains(dep))
                    {
                        usedSet.Add(dep);
                        sceneHits.Add(dep);
                    }
                }

                if (ap.EndsWith(".unity") && sceneHits.Count > 0)
                    sceneCrossRef[ap] = sceneHits;

                if (i % CHUNK == 0)
                {
                    status = $"Scanning… {i}/{total}  (dep cache: {DependencyCache.CacheCount})";
                    yield return null;
                }
            }

            // ── Phase 4: Categorise results ──────────────────────────────────────

            _phase = "Categorising";
            _progress = 0.85f;

            yield return null;

            if (single)
            {
                foreach (var fp in targets)
                {
                    known.Add(fp);

                    if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(fp)))
                    {
                        noGuid.Add(fp);
                        continue;
                    }

                    // Skip assets already sitting in the bin — they stay there
                    if (bin.Contains(fp)) continue;

                    long bytes = new FileInfo(fp).Length;

                    if (usedSet.Contains(fp))
                    {
                        sfUsed.Add(fp);
                        selSfU[fp] = true;
                        szSfU += bytes;
                    }
                    else
                    {
                        sfUnused.Add(fp);
                        selSfN[fp] = true;
                        szSfN += bytes;
                    }
                }

                sfUsed.Sort(System.StringComparer.OrdinalIgnoreCase);
                sfUnused.Sort(System.StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                // Phase 4b: content-match folder 2 against f1ByHash
                if (useHashDetect)
                {
                    _phase = "Matching content";
                    _progress = 0.88f;

                    int hi = 0;
                    foreach (var fp in targets)
                    {
                        // Same normalised hash as used when building f1ByHash —
                        // strips m_Name so renamed YAML duplicates are correctly matched.
                        string h = FileHashCache.GetNormalised(fp);
                        if (!string.IsNullOrEmpty(h) && f1ByHash.TryGetValue(h, out string mirror))
                            contentMirror[fp] = mirror;

                        if (++hi % 50 == 0)
                            yield return null;
                    }
                }

                foreach (var kvp in f2ByRel)
                {
                    string rel = kvp.Key;
                    string fp = kvp.Value;

                    known.Add(fp);

                    if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(fp)))
                    {
                        noGuid.Add(fp);
                        continue;
                    }

                    // Skip assets already in the bin — they are shown there instead
                    if (bin.Contains(fp)) continue;

                    long bytes = new FileInfo(fp).Length;
                    bool isUsed = usedSet.Contains(fp);
                    bool hasMirr = f1ByRel.ContainsKey(rel) || contentMirror.ContainsKey(fp);

                    if (isUsed && hasMirr)
                    {
                        duplicates.Add(fp);
                        selDup[fp] = true;
                        szDup += bytes;
                    }
                    else if (isUsed)
                    {
                        uniqueUsed.Add(fp);
                        selUnq[fp] = true;
                        szUnq += bytes;
                    }
                    else
                    {
                        unused.Add(fp);
                        selUnu[fp] = true;
                        szUnu += bytes;
                    }
                }

                duplicates.Sort(System.StringComparer.OrdinalIgnoreCase);
                uniqueUsed.Sort(System.StringComparer.OrdinalIgnoreCase);
                unused.Sort(System.StringComparer.OrdinalIgnoreCase);
            }

            // ── Phase 5: Done ────────────────────────────────────────────────────

            _progress = 1f;
            _phase = "Complete";
            lastScan = EditorApplication.timeSinceStartup;
            _scanning = false;

            string guidNote = guidCollisions.Count > 0 ? $"  ⚠ {guidCollisions.Count} GUID collision(s)." : "";
            string orphNote = orphanedMetas.Count > 0 ? $"  ⚠ {orphanedMetas.Count} orphaned .meta file(s)." : "";
            string matchNote = useHashDetect ? $"  Content matches: {contentMirror.Count}." : "";

            status = single
                ? $"Scan complete — {sfUsed.Count} used {FormatBytes(szSfU)}, {sfUnused.Count} unused {FormatBytes(szSfN)}.{guidNote}{orphNote}"
                : $"Scan complete — {duplicates.Count} dupes {FormatBytes(szDup)}, {uniqueUsed.Count} unique {FormatBytes(szUnq)}, {unused.Count} unused {FormatBytes(szUnu)}.{matchNote}{guidNote}{orphNote}";
        }

        private static string ExtractGuid(string metaPath)
        {
            try
            {
                foreach (var line in File.ReadLines(metaPath))
                {
                    if (line.StartsWith("guid:"))
                        return line.Substring(5).Trim();
                }
            }
            catch { }

            return "";
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Swap — selected
        // ─────────────────────────────────────────────────────────────────────────

        private string GetMirror(string asset)
        {
            if (folder2.Length + 1 < asset.Length)
            {
                string rel = asset.Substring(folder2.Length + 1);
                if (f1ByRel.TryGetValue(rel, out string m))
                    return m;
            }

            contentMirror.TryGetValue(asset, out string cm);
            return cm;
        }

        private void ConfirmSwap()
        {
            var selected = duplicates
                .Where(a => selDup.ContainsKey(a) && selDup[a] && PassesFilter(a))
                .ToList();

            if (selected.Count == 0)
            {
                status = "No assets selected.";
                return;
            }

            var preview = selected
                .Select(a => (asset: a, mirror: GetMirror(a), changes: CollectChanges(a, GetMirror(a))))
                .Where(t => t.mirror != null)
                .ToList();

            int propCount = preview.Sum(p => p.changes.Count);
            int fileCount = preview.SelectMany(p => p.changes).Select(p => p.assetPath).Distinct().Count();

            string msg = $"Swap {selected.Count} asset(s)?\n\nWill update {propCount} propert{(propCount == 1 ? "y" : "ies")} across {fileCount} file(s).\n\n";

            foreach (var group in preview.SelectMany(p => p.changes).GroupBy(p => p.assetPath).Take(6))
                msg += $"  {Path.GetFileName(group.Key)}  ({group.Count()})\n";

            if (fileCount > 6)
                msg += "  …and more.\n";

            if (EditorUtility.DisplayDialog("Confirm Swap", msg, "Swap", "Cancel"))
                DoSwap(selected);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Swap — all duplicates at once
        // ─────────────────────────────────────────────────────────────────────────

        private void ConfirmSwapAll()
        {
            if (duplicates.Count == 0)
            {
                status = "No duplicates to swap.";
                return;
            }

            var all = duplicates.Where(a => PassesFilter(a)).ToList();

            if (all.Count == 0)
            {
                status = "No duplicates match the current filter.";
                return;
            }

            var preview = all
                .Select(a => (asset: a, mirror: GetMirror(a), changes: CollectChanges(a, GetMirror(a))))
                .Where(t => t.mirror != null)
                .ToList();

            int propCount = preview.Sum(p => p.changes.Count);
            int fileCount = preview.SelectMany(p => p.changes).Select(p => p.assetPath).Distinct().Count();

            string msg = $"Swap ALL {all.Count} duplicate(s)?\n\nWill update {propCount} propert{(propCount == 1 ? "y" : "ies")} across {fileCount} file(s).\n\nThis cannot be undone from the history panel once the Bin is emptied.";

            if (EditorUtility.DisplayDialog("Confirm Swap All", msg, "Swap All", "Cancel"))
                DoSwap(all);
        }

        private void DoSwap(List<string> selected)
        {
            status = "Swapping…";

            var rec = new ActionRecord { type = ActionType.Swap };

            foreach (var asset in selected)
            {
                string mirror = GetMirror(asset);
                if (mirror == null) continue;

                EnsureCheckedOut(mirror);

                var props = ReplaceRefs(asset, mirror);
                rec.swapDetails.Add((asset, mirror, props));

                duplicates.Remove(asset);
                selDup.Remove(asset);

                Debug.Log($"[ADR] Swapped: {asset} → {mirror}");
            }

            if (rec.swapDetails.Count > 0)
                history.Add(rec);

            status = $"Swap complete — {rec.swapDetails.Count} asset(s) redirected.";

            refCache.Clear();
            MaybeSave();
            AssetDatabase.Refresh();
        }

        private void BinSelectedDuplicates()
        {
            var selected = duplicates
                .Where(a => selDup.ContainsKey(a) && selDup[a] && PassesFilter(a))
                .ToList();

            if (selected.Count == 0)
            {
                status = "No duplicates selected.";
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "Bin Duplicates",
                $"Move {selected.Count} duplicate(s) to Bin without swapping references?\nExisting references will break.",
                "Bin Anyway",
                "Cancel");

            if (!confirmed) return;

            var rec = new ActionRecord { type = ActionType.Bin };

            foreach (var a in selected)
            {
                EnsureCheckedOut(a);
                if (!bin.Contains(a)) bin.Add(a);
                binSource[a] = BinSource.Duplicates;
                rec.deletedAssets.Add((a, BinSource.Duplicates));
                duplicates.Remove(a);
                selDup.Remove(a);
            }

            if (rec.deletedAssets.Count > 0)
                history.Add(rec);

            status = $"{selected.Count} duplicate(s) moved to Bin.";
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Move unique used assets
        // ─────────────────────────────────────────────────────────────────────────

        private void MoveUnique()
        {
            var selected = uniqueUsed
                .Where(a => selUnq.ContainsKey(a) && selUnq[a] && PassesFilter(a))
                .ToList();

            if (selected.Count == 0)
            {
                status = "No assets selected.";
                return;
            }

            string targetFolder = folder1 + "/MigratedFromFolder2";

            if (!AssetDatabase.IsValidFolder(targetFolder))
                AssetDatabase.CreateFolder(folder1, "MigratedFromFolder2");

            var rec = new ActionRecord { type = ActionType.Move };

            foreach (var a in selected)
            {
                EnsureCheckedOut(a);

                string newPath = ResolveConflictName(targetFolder, Path.GetFileName(a));
                string error = AssetDatabase.MoveAsset(a, newPath);

                if (string.IsNullOrEmpty(error))
                {
                    rec.moveRecords.Add((a, newPath));
                    uniqueUsed.Remove(a);
                    selUnq.Remove(a);
                    known.Remove(a);
                    known.Add(newPath);
                }
                else
                {
                    Debug.LogError($"[ADR] Move failed: {a} — {error}");
                }
            }

            if (rec.moveRecords.Count > 0)
                history.Add(rec);

            status = $"Moved {rec.moveRecords.Count} asset(s).";

            MaybeSave();
            AssetDatabase.Refresh();
        }

        private string ResolveConflictName(string folder, string fileName)
        {
            string candidate = folder + "/" + fileName;
            if (!File.Exists(candidate)) return candidate;

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int suffix = 1;

            while (File.Exists($"{folder}/{baseName}_{suffix}{ext}"))
                suffix++;

            return $"{folder}/{baseName}_{suffix}{ext}";
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Bin helpers
        // ─────────────────────────────────────────────────────────────────────────

        private void BinList(List<string> list, Dictionary<string, bool> sel, BinSource source)
        {
            // Only bin items that are currently visible (pass filter + threshold) AND checked.
            // Items hidden by the filter keep their checkbox state untouched.
            var selected = list
                .Where(a => sel.ContainsKey(a) && sel[a] && PassesFilter(a) && PassesThreshold(a))
                .ToList();

            if (selected.Count == 0)
            {
                status = "No assets selected.";
                return;
            }

            var rec = new ActionRecord { type = ActionType.Bin };

            foreach (var a in selected)
            {
                EnsureCheckedOut(a);
                if (!bin.Contains(a)) bin.Add(a);
                binSource[a] = source;
                rec.deletedAssets.Add((a, source));
                list.Remove(a);
                sel.Remove(a);
            }

            if (rec.deletedAssets.Count > 0)
                history.Add(rec);

            status = $"{selected.Count} asset(s) moved to Bin.";
        }

        private void EmptyBin()
        {
            foreach (var a in bin.ToList())
            {
                EnsureCheckedOut(a);

                if (AssetDatabase.DeleteAsset(a))
                {
                    DependencyCache.Invalidate(a);
                    FileHashCache.Invalidate(a);
                    bin.Remove(a);
                    binSource.Remove(a);

                    // Remove from any scan list it may still appear in after a rescan
                    RemoveFromAllLists(a);
                }
            }

            MaybeSave();
            AssetDatabase.Refresh();
        }

        private void RemoveFromAllLists(string a)
        {
            duplicates.Remove(a); selDup.Remove(a);
            uniqueUsed.Remove(a); selUnq.Remove(a);
            unused.Remove(a); selUnu.Remove(a);
            sfUsed.Remove(a); selSfU.Remove(a);
            sfUnused.Remove(a); selSfN.Remove(a);
        }

        private void RestoreFromBin(string a)
        {
            bin.Remove(a);
            RestoreToSourceList(a);
            binSource.Remove(a);
        }

        private void RestoreAllFromBin()
        {
            foreach (var a in bin.ToList())
                RestoreToSourceList(a);

            bin.Clear();
            binSource.Clear();

            duplicates.Sort(System.StringComparer.OrdinalIgnoreCase);
            uniqueUsed.Sort(System.StringComparer.OrdinalIgnoreCase);
            unused.Sort(System.StringComparer.OrdinalIgnoreCase);
            sfUsed.Sort(System.StringComparer.OrdinalIgnoreCase);
            sfUnused.Sort(System.StringComparer.OrdinalIgnoreCase);
        }

        private void RestoreToSourceList(string a)
        {
            BinSource src = binSource.TryGetValue(a, out var s) ? s : BinSource.Unused;

            switch (src)
            {
                case BinSource.Duplicates:
                    if (!duplicates.Contains(a)) { duplicates.Add(a); selDup[a] = true; }
                    break;
                case BinSource.UniqueUsed:
                    if (!uniqueUsed.Contains(a)) { uniqueUsed.Add(a); selUnq[a] = true; }
                    break;
                case BinSource.SfUsed:
                    if (!sfUsed.Contains(a)) { sfUsed.Add(a); selSfU[a] = true; }
                    break;
                case BinSource.SfUnused:
                    if (!sfUnused.Contains(a)) { sfUnused.Add(a); selSfN[a] = true; }
                    break;
                default: // Unused
                    if (!unused.Contains(a)) { unused.Add(a); selUnu[a] = true; }
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Export report
        // ─────────────────────────────────────────────────────────────────────────

        private void ExportReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Asset Duplicate Resolver — Scan Report");
            sb.AppendLine("Created by AL!Z (Aliz Pasztor) — alizpasztor.com");
            sb.AppendLine($"Date: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Mode: {(Single ? "Single-folder" : "Two-folder")}  |  Content match: {useHashDetect}");
            sb.AppendLine($"Folder 1: {folder1}");

            if (!Single)
                sb.AppendLine($"Folder 2: {folder2}");

            if (guidCollisions.Count > 0)
                sb.AppendLine($"GUID Collisions: {guidCollisions.Count}");

            if (orphanedMetas.Count > 0)
                sb.AppendLine($"Orphaned .meta files: {orphanedMetas.Count}");

            sb.AppendLine();
            sb.AppendLine("Category,Path,Size,Content Match,Refs");

            void WriteRows(string category, List<string> list)
            {
                foreach (var a in list)
                {
                    int refCount = refCache.TryGetValue(a, out var r) ? r.Count : 0;
                    bool hasMatch = contentMirror.ContainsKey(a);
                    sb.AppendLine($"{category},\"{a}\",{SizeString(a)},{(hasMatch ? "yes" : "no")},{refCount}");
                }
            }

            if (Single)
            {
                WriteRows("Used", sfUsed);
                WriteRows("Unused", sfUnused);
            }
            else
            {
                WriteRows("Duplicate", duplicates);
                WriteRows("UniqueUsed", uniqueUsed);
                WriteRows("Unused", unused);
            }

            if (orphanedMetas.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("OrphanedMeta,Path,,,,");
                foreach (var m in orphanedMetas)
                    sb.AppendLine($"OrphanedMeta,\"{m}\",,,,");
            }

            string outputPath = $"Assets/ADR_Report_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv";
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            AssetDatabase.Refresh();

            var obj = AssetDatabase.LoadAssetAtPath<Object>(outputPath);
            if (obj != null)
            {
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }

            status = $"Report exported: {outputPath}";
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Undo
        // ─────────────────────────────────────────────────────────────────────────

        private void ReverseAction(ActionRecord rec)
        {
            switch (rec.type)
            {
                case ActionType.Swap:
                    foreach (var (originalPath, newPath, props) in rec.swapDetails)
                    {
                        if (props?.Count > 0)
                            RestoreProps(props, newPath, originalPath);
                        else
                            ReplaceRefs(newPath, originalPath);

                        DependencyCache.Invalidate(originalPath);
                        DependencyCache.Invalidate(newPath);

                        // Put the asset back in the duplicates list so the user can see it was restored
                        if (!duplicates.Contains(originalPath))
                        {
                            duplicates.Add(originalPath);
                            selDup[originalPath] = true;
                        }
                    }
                    duplicates.Sort(System.StringComparer.OrdinalIgnoreCase);
                    break;

                case ActionType.Move:
                    foreach (var (originalPath, movedPath) in rec.moveRecords)
                    {
                        AssetDatabase.MoveAsset(movedPath, originalPath);
                        known.Remove(movedPath);
                        known.Add(originalPath);
                        DependencyCache.Invalidate(movedPath);
                        DependencyCache.Invalidate(originalPath);
                    }
                    break;

                case ActionType.Bin:
                    foreach (var (path, src) in rec.deletedAssets)
                    {
                        bin.Remove(path);
                        binSource.Remove(path);
                        RestoreToSourceList(path);
                    }
                    break;
            }

            refCache.Clear();
            MaybeSave();
            AssetDatabase.Refresh();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Reference replacement
        // ─────────────────────────────────────────────────────────────────────────

        private List<PropertyChange> CollectChanges(string oldAsset, string newAsset)
        {
            var changes = new List<PropertyChange>();

            if (string.IsNullOrEmpty(newAsset)) return changes;

            Object newObj = AssetDatabase.LoadAssetAtPath<Object>(newAsset);
            if (!newObj) return changes;

            foreach (var ap in AssetDatabase.GetAllAssetPaths())
            {
                if (!ap.StartsWith("Assets/") || AssetDatabase.IsValidFolder(ap) || ap == oldAsset) continue;
                if (!DependencyCache.Get(ap, !swapImmOnly).Contains(oldAsset)) continue;

                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(ap))
                {
                    if (!obj) continue;

                    var so = new SerializedObject(obj);
                    var sp = so.GetIterator();

                    while (sp.NextVisible(true))
                    {
                        if (sp.propertyType == SerializedPropertyType.ObjectReference
                            && sp.objectReferenceValue
                            && AssetDatabase.GetAssetPath(sp.objectReferenceValue) == oldAsset)
                        {
                            changes.Add(new PropertyChange
                            {
                                assetPath = ap,
                                propertyPath = sp.propertyPath,
                                oldValue = oldAsset,
                                newValue = newAsset
                            });
                        }
                    }
                }
            }

            return changes;
        }

        private List<PropertyChange> ReplaceRefs(string oldAsset, string newAsset)
        {
            var changes = new List<PropertyChange>();

            Object newObj = AssetDatabase.LoadAssetAtPath<Object>(newAsset);
            if (!newObj) return changes;

            string activeScene = EditorSceneManager.GetActiveScene().path;

            foreach (var ap in AssetDatabase.GetAllAssetPaths())
            {
                if (!ap.StartsWith("Assets/") || AssetDatabase.IsValidFolder(ap) || ap == oldAsset) continue;
                if (!DependencyCache.Get(ap, !swapImmOnly).Contains(oldAsset)) continue;

                if (ap.EndsWith(".unity"))
                {
                    // Scenes cannot be swapped via LoadAllAssetsAtPath — must be opened.
                    changes.AddRange(SwapRefsInScene(ap, oldAsset, newAsset, newObj, activeScene));
                }
                else
                {
                    foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(ap))
                    {
                        if (!obj) continue;

                        var so = new SerializedObject(obj);
                        var sp = so.GetIterator();
                        bool dirty = false;

                        while (sp.NextVisible(true))
                        {
                            if (sp.propertyType == SerializedPropertyType.ObjectReference
                                && sp.objectReferenceValue
                                && AssetDatabase.GetAssetPath(sp.objectReferenceValue) == oldAsset)
                            {
                                changes.Add(new PropertyChange
                                {
                                    assetPath = ap,
                                    propertyPath = sp.propertyPath,
                                    oldValue = oldAsset,
                                    newValue = newAsset
                                });

                                sp.objectReferenceValue = newObj;
                                dirty = true;
                            }
                        }

                        if (dirty)
                        {
                            so.ApplyModifiedPropertiesWithoutUndo();
                            EditorUtility.SetDirty(obj);
                            DependencyCache.Invalidate(ap);
                        }
                    }
                }
            }

            return changes;
        }

        // Swaps references inside a scene file.
        // If the scene is already open we work in-place; otherwise we open it additively,
        // swap, save, then close it — leaving the user's workspace unchanged.
        private List<PropertyChange> SwapRefsInScene(
            string scenePath,
            string oldAsset,
            string newAsset,
            Object newObj,
            string activeScene)
        {
            var changes = new List<PropertyChange>();
            bool wasOpen = scenePath == activeScene;
            bool opened = false;

            try
            {
                if (!wasOpen)
                {
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                    opened = true;
                }

                var scene = EditorSceneManager.GetSceneByPath(scenePath);
                if (!scene.IsValid() || !scene.isLoaded) return changes;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var comp in root.GetComponentsInChildren<Component>(true))
                    {
                        if (!comp) continue;

                        var so = new SerializedObject(comp);
                        var sp = so.GetIterator();
                        bool dirty = false;

                        while (sp.NextVisible(true))
                        {
                            if (sp.propertyType == SerializedPropertyType.ObjectReference
                                && sp.objectReferenceValue
                                && AssetDatabase.GetAssetPath(sp.objectReferenceValue) == oldAsset)
                            {
                                changes.Add(new PropertyChange
                                {
                                    assetPath = scenePath,
                                    propertyPath = sp.propertyPath,
                                    oldValue = oldAsset,
                                    newValue = newAsset
                                });

                                sp.objectReferenceValue = newObj;
                                dirty = true;
                            }
                        }

                        if (dirty)
                        {
                            so.ApplyModifiedPropertiesWithoutUndo();
                            EditorUtility.SetDirty(comp);
                        }
                    }
                }

                if (changes.Count > 0)
                {
                    EditorSceneManager.SaveScene(scene);
                    DependencyCache.Invalidate(scenePath);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ADR] Scene swap failed for {scenePath}: {ex.Message}");
            }
            finally
            {
                if (opened)
                {
                    var scene = EditorSceneManager.GetSceneByPath(scenePath);
                    if (scene.IsValid())
                        EditorSceneManager.CloseScene(scene, true);
                }
            }

            return changes;
        }

        private void RestoreProps(List<PropertyChange> changes, string fromAsset, string toAsset)
        {
            Object restoreObj = AssetDatabase.LoadAssetAtPath<Object>(toAsset);

            if (!restoreObj)
            {
                // Fallback: brute-force re-scan (slower but reliable)
                ReplaceRefs(fromAsset, toAsset);
                return;
            }

            // Group changes by asset path so we only open each file once
            string activeScene = EditorSceneManager.GetActiveScene().path;

            var byFile = changes.GroupBy(c => c.assetPath);

            foreach (var group in byFile)
            {
                string ap = group.Key;

                if (ap.EndsWith(".unity"))
                {
                    // Scenes need to be open to swap references
                    RestorePropsInScene(ap, group.ToList(), fromAsset, restoreObj, activeScene);
                }
                else
                {
                    foreach (var change in group)
                    {
                        foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(ap))
                        {
                            if (!obj) continue;

                            var so = new SerializedObject(obj);
                            var sp = so.FindProperty(change.propertyPath);

                            if (sp == null || sp.propertyType != SerializedPropertyType.ObjectReference) continue;
                            if (AssetDatabase.GetAssetPath(sp.objectReferenceValue) != fromAsset) continue;

                            sp.objectReferenceValue = restoreObj;
                            so.ApplyModifiedPropertiesWithoutUndo();
                            EditorUtility.SetDirty(obj);
                            DependencyCache.Invalidate(ap);
                        }
                    }
                }
            }
        }

        private void RestorePropsInScene(
            string scenePath,
            List<PropertyChange> changes,
            string fromAsset,
            Object restoreObj,
            string activeScene)
        {
            bool wasOpen = scenePath == activeScene;
            bool opened = false;

            try
            {
                if (!wasOpen)
                {
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                    opened = true;
                }

                var scene = EditorSceneManager.GetSceneByPath(scenePath);
                if (!scene.IsValid() || !scene.isLoaded) return;

                // Build a set of property paths to restore for fast lookup
                var pathSet = new HashSet<string>(changes.Select(c => c.propertyPath));
                bool dirty = false;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var comp in root.GetComponentsInChildren<Component>(true))
                    {
                        if (!comp) continue;

                        var so = new SerializedObject(comp);
                        var sp = so.GetIterator();
                        bool compDirty = false;

                        while (sp.NextVisible(true))
                        {
                            if (sp.propertyType != SerializedPropertyType.ObjectReference) continue;
                            if (!pathSet.Contains(sp.propertyPath)) continue;
                            if (!sp.objectReferenceValue) continue;
                            if (AssetDatabase.GetAssetPath(sp.objectReferenceValue) != fromAsset) continue;

                            sp.objectReferenceValue = restoreObj;
                            compDirty = true;
                            dirty = true;
                        }

                        if (compDirty)
                        {
                            so.ApplyModifiedPropertiesWithoutUndo();
                            EditorUtility.SetDirty(comp);
                        }
                    }
                }

                if (dirty)
                {
                    EditorSceneManager.SaveScene(scene);
                    DependencyCache.Invalidate(scenePath);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ADR] Undo scene restore failed for {scenePath}: {ex.Message}");
            }
            finally
            {
                if (opened)
                {
                    var scene = EditorSceneManager.GetSceneByPath(scenePath);
                    if (scene.IsValid())
                        EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Reference tooltip
        // ─────────────────────────────────────────────────────────────────────────

        private string BuildRefTooltip(string path)
        {
            if (!refCache.TryGetValue(path, out var refs))
            {
                refs = new List<string>();

                foreach (var p in AssetDatabase.GetAllAssetPaths())
                {
                    if (!p.StartsWith("Assets/") || AssetDatabase.IsValidFolder(p)) continue;
                    if (p == path) continue;

                    if (DependencyCache.Get(p, true).Contains(path))
                        refs.Add(p);
                }

                refCache[path] = refs;
            }

            if (refs.Count == 0)
                return "Not referenced anywhere";

            return "Referenced by:\n" + string.Join("\n", refs.Distinct().Take(20));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────────

        private void MaybeSave()
        {
            if (autoSave)
                AssetDatabase.SaveAssets();
        }

        private void EnsureCheckedOut(string path)
        {
            if (!Provider.enabled || !Provider.isActive) return;

            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj) Provider.Checkout(obj, CheckoutMode.Asset).Wait();
        }

        private string SizeString(string path)
        {
            var fi = new FileInfo(path);
            return fi.Exists ? FormatBytes(fi.Length) : "(missing)";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"({bytes / (1024f * 1024f):F1} MB)";

            if (bytes >= 1024)
                return $"({bytes / 1024f:F1} KB)";

            return $"({bytes} B)";
        }

        private bool PassesFilter(string path)
        {
            return string.IsNullOrEmpty(filter)
                || path.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool PassesThreshold(string path)
        {
            if (sizeThreshold <= 0) return true;

            var fi = new FileInfo(path);
            return fi.Exists && fi.Length >= sizeThreshold;
        }

        private int FilteredCount(List<string> list)
        {
            if (string.IsNullOrEmpty(filter) && sizeThreshold <= 0)
                return list.Count;

            return list.Count(a => PassesFilter(a) && PassesThreshold(a));
        }

        private bool IsIgnored(string path)
        {
            return ignoreFolders.Any(f => path.StartsWith(f + "/") || path == f);
        }

        private string ShortName(string path)
        {
            var parts = path.Split('/');
            return parts.Length > 1 ? parts[parts.Length - 1] : path;
        }

        private void InitStyles()
        {
            if (_stylesOk) return;

            _hdr = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Pal.Text }
            };

            _sec = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                normal = { textColor = Pal.Text }
            };

            _path = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10,
                wordWrap = false,
                normal = { textColor = Pal.TextDim }
            };

            _dim = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = Pal.ElectricBlue }
            };

            _stylesOk = true;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Data types
        // ─────────────────────────────────────────────────────────────────────────

        private class PropertyChange
        {
            public string assetPath;
            public string propertyPath;
            public string oldValue;
            public string newValue;
        }

        private class ActionRecord
        {
            public ActionType type;

            public List<(string op, string np, List<PropertyChange> props)> swapDetails = new List<(string, string, List<PropertyChange>)>();
            public List<(string op, string np)> moveRecords = new List<(string, string)>();

            // Each binned asset remembers which category it came from so Restore puts it back correctly
            public List<(string path, BinSource source)> deletedAssets = new List<(string, BinSource)>();

            public string Summary
            {
                get
                {
                    return type switch
                    {
                        ActionType.Swap => $"Swap — {swapDetails.Count}",
                        ActionType.Move => $"Move — {moveRecords.Count}",
                        ActionType.Bin => $"Bin — {deletedAssets.Count}",
                        _ => "Unknown"
                    };
                }
            }
        }

        private enum ActionType { Swap, Move, Bin }
        private enum BinSource { Duplicates, UniqueUsed, Unused, SfUsed, SfUnused }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Reference Ping Window
    // ─────────────────────────────────────────────────────────────────────────────

    public class ReferencePingWindow : EditorWindow
    {
        private string targetAsset;
        private List<string> refs;
        private Vector2 scroll;

        public static void Show(string assetPath, List<string> references)
        {
            var w = CreateInstance<ReferencePingWindow>();
            w.targetAsset = assetPath;
            w.refs = references;
            w.titleContent = new GUIContent("References");
            w.minSize = new Vector2(460, 220);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            Rect hdr = EditorGUILayout.GetControlRect(false, 36);
            EditorGUI.DrawRect(hdr, Pal.BgDark);
            Pal.DrawGradientBar(new Rect(hdr.x, hdr.yMax - 2, hdr.width, 2), Pal.Orange, Pal.Pink);
            EditorGUI.DrawRect(new Rect(hdr.x, hdr.y, 4, hdr.height), Pal.AccentUniq);
            var hdrStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Pal.Text }, fontSize = 12 };
            var subStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Pal.TextDim }, wordWrap = true };
            GUI.Label(new Rect(hdr.x + 10, hdr.y + 4, hdr.width - 16, 16), "References", hdrStyle);
            GUI.Label(new Rect(hdr.x + 10, hdr.y + 20, hdr.width - 16, 14), targetAsset, subStyle);
            GUILayout.Space(6);

            string activeScene = EditorSceneManager.GetActiveScene().path;
            scroll = EditorGUILayout.BeginScrollView(scroll);

            foreach (var r in refs)
            {
                bool isOpen = !string.IsNullOrEmpty(activeScene) && r == activeScene;

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(r, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                if (GUILayout.Button("Ping", GUILayout.Width(44)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<Object>(r);
                    if (obj) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
                }

                if (GUILayout.Button("Select", GUILayout.Width(52)))
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<Object>(r);

                if (isOpen && GUILayout.Button("Find in Scene", GUILayout.Width(96)))
                    PingActive(targetAsset);

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
            GUILayout.Space(4);

            if (GUILayout.Button("Close"))
                Close();
        }

        private void PingActive(string assetPath)
        {
            var hits = new List<GameObject>();

            foreach (var comp in Object.FindObjectsByType<Component>(FindObjectsSortMode.None))
            {
                if (!comp) continue;

                var so = new SerializedObject(comp);
                var sp = so.GetIterator();

                while (sp.NextVisible(true))
                {
                    if (sp.propertyType == SerializedPropertyType.ObjectReference
                        && sp.objectReferenceValue
                        && AssetDatabase.GetAssetPath(sp.objectReferenceValue) == assetPath)
                    {
                        hits.Add(comp.gameObject);
                        break;
                    }
                }
            }

            if (hits.Count == 0)
            {
                Debug.Log($"[ADR] No objects in active scene reference: {assetPath}");
                return;
            }

            Selection.objects = hits.Select(g => (Object)g).ToArray();
            EditorGUIUtility.PingObject(hits[0]);
            Debug.Log($"[ADR] Selected {hits.Count} object(s) referencing {assetPath}.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Dependency Graph Window
    // ─────────────────────────────────────────────────────────────────────────────

    public class DependencyGraphWindow : EditorWindow
    {
        private string root;
        private int depth;
        private Vector2 scroll;

        public static void Show(string path, int d)
        {
            var w = CreateInstance<DependencyGraphWindow>();
            w.root = path;
            w.depth = d;
            w.titleContent = new GUIContent("Deps");
            w.minSize = new Vector2(420, 300);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            Rect hdr = EditorGUILayout.GetControlRect(false, 36);
            EditorGUI.DrawRect(hdr, Pal.BgDark);
            Pal.DrawGradientBar(new Rect(hdr.x, hdr.yMax - 2, hdr.width, 2), Pal.Orange, Pal.Pink);
            EditorGUI.DrawRect(new Rect(hdr.x, hdr.y, 4, hdr.height), Pal.Cyan);
            var hdrStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Pal.Text }, fontSize = 12 };
            var subStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Pal.TextDim }, wordWrap = true };
            GUI.Label(new Rect(hdr.x + 10, hdr.y + 4, hdr.width - 16, 16), "Dependency Graph", hdrStyle);
            GUI.Label(new Rect(hdr.x + 10, hdr.y + 20, hdr.width - 16, 14), root, subStyle);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Max depth: {depth}", GUILayout.Width(90));
            depth = (int)GUILayout.HorizontalSlider(depth, 1, 10, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(6);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawNode(root, 0, new HashSet<string>());
            EditorGUILayout.EndScrollView();

            GUILayout.Space(4);
            if (GUILayout.Button("Close")) Close();
        }

        private void DrawNode(string path, int d, HashSet<string> visited)
        {
            if (d > depth || visited.Contains(path)) return;

            visited.Add(path);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(d * 12);

            Texture icon = AssetDatabase.GetCachedIcon(path);
            if (icon != null)
                GUILayout.Label(icon, GUILayout.Width(16), GUILayout.Height(16));

            var style = d == 0
                ? EditorStyles.boldLabel
                : d < depth
                    ? EditorStyles.label
                    : EditorStyles.miniLabel;

            if (GUILayout.Button(new GUIContent(Path.GetFileName(path), path), style, GUILayout.ExpandWidth(false)))
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (obj) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
            }

            EditorGUILayout.EndHorizontal();

            if (d < depth)
            {
                foreach (var dep in DependencyCache.Get(path, false))
                {
                    if (dep != path)
                        DrawNode(dep, d + 1, visited);
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Asset Diff Window
    // ─────────────────────────────────────────────────────────────────────────────

    public class AssetDiffWindow : EditorWindow
    {
        private string pathA;
        private string pathB;
        private string hashA;
        private string hashB;
        private Vector2 scroll;

        public static void Show(string a, string b)
        {
            var w = CreateInstance<AssetDiffWindow>();
            w.pathA = a;
            w.pathB = b;
            w.hashA = FileHashCache.Get(a);
            w.hashB = FileHashCache.Get(b);
            w.titleContent = new GUIContent("Diff");
            w.minSize = new Vector2(560, 380);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            Rect hdr = EditorGUILayout.GetControlRect(false, 36);
            EditorGUI.DrawRect(hdr, Pal.BgDark);
            Pal.DrawGradientBar(new Rect(hdr.x, hdr.yMax - 2, hdr.width, 2), Pal.Orange, Pal.Pink);
            EditorGUI.DrawRect(new Rect(hdr.x, hdr.y, 4, hdr.height), Pal.Pink);
            var hdrStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Pal.Text }, fontSize = 12 };
            GUI.Label(new Rect(hdr.x + 10, hdr.y + 10, hdr.width - 16, 18), "Asset Diff", hdrStyle);

            GUILayout.Space(4);

            bool identical = !string.IsNullOrEmpty(hashA) && hashA == hashB;

            Rect sr = EditorGUILayout.GetControlRect(false, 22);
            EditorGUI.DrawRect(sr, Pal.BgPanel);
            Color matchCol = identical ? Pal.Cyan : Pal.Orange;
            EditorGUI.DrawRect(new Rect(sr.x, sr.y, 4, sr.height), matchCol);
            var sumStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = matchCol }
            };
            GUI.Label(new Rect(sr.x + 8, sr.y + 4, sr.width - 12, 14),
                identical ? "✓  Byte-for-byte identical (same MD5)" : "≠  Files differ in content",
                sumStyle);

            GUILayout.Space(4);

            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.BeginHorizontal();
            DrawColumn("Folder 2 (duplicate)", pathA, hashA);
            GUILayout.Space(6);
            DrawColumn("Folder 1 (source)", pathB, hashB);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();

            GUILayout.Space(4);
            if (GUILayout.Button("Close")) Close();
        }

        private void DrawColumn(string header, string path, string hash)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandWidth(true));

            GUILayout.Label(header, EditorStyles.boldLabel);
            GUILayout.Label(Path.GetFileName(path), EditorStyles.miniLabel);
            GUILayout.Space(4);

            if (!File.Exists(path))
            {
                GUILayout.Label("(file not found)", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            var fi = new FileInfo(path);
            string ext = fi.Extension.ToLower();

            DrawRow("File size", FormatBytes(fi.Length));
            DrawRow("Modified", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
            DrawRow("MD5", string.IsNullOrEmpty(hash) ? "n/a" : hash.Substring(0, 8) + "…");

            // ── Texture ──────────────────────────────────────────────────────────

            var textureImporter = AssetImporter.GetAtPath(path) as TextureImporter;
            if (textureImporter != null)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex != null)
                {
                    DrawRow("Dimensions", $"{tex.width} × {tex.height}");
                    DrawRow("Format", tex.format.ToString());
                    DrawRow("Mip maps", tex.mipmapCount > 1 ? "Yes" : "No");
                    DrawRow("sRGB", textureImporter.sRGBTexture ? "Yes" : "No");
                    DrawRow("Compression", textureImporter.textureCompression.ToString());

                    var otherPath = path == pathA ? pathB : pathA;
                    var other = AssetDatabase.LoadAssetAtPath<Texture2D>(otherPath);

                    if (other != null)
                    {
                        if (tex.width == other.width && tex.height == other.height)
                        {
                            float sim = ComputePixelSimilarity(tex, other);
                            string simLabel = sim == -2f
                                ? "Enable Read/Write in Import Settings"
                                : sim < 0f
                                    ? "Unavailable"
                                    : $"{sim:F1}%";
                            DrawRow("Pixel similarity", simLabel);
                        }
                        else
                        {
                            DrawRow("Pixel similarity", "N/A — different dimensions");
                        }
                    }

                    var preview = AssetPreview.GetAssetPreview(tex);
                    if (preview != null)
                    {
                        GUILayout.Space(4);
                        GUI.DrawTexture(EditorGUILayout.GetControlRect(false, 80), preview, ScaleMode.ScaleToFit);
                    }
                }
            }

            // ── Audio ─────────────────────────────────────────────────────────────

            var audioImporter = AssetImporter.GetAtPath(path) as AudioImporter;
            if (audioImporter != null)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null)
                {
                    DrawRow("Duration", $"{clip.length:F2}s");
                    DrawRow("Channels", clip.channels.ToString());
                    DrawRow("Frequency", $"{clip.frequency} Hz");
                    DrawRow("Load type", audioImporter.defaultSampleSettings.loadType.ToString());
                    DrawRow("Compression", audioImporter.defaultSampleSettings.compressionFormat.ToString());
                    GUILayout.Space(4);
                    GUILayout.Label("Waveform:", EditorStyles.miniLabel);
                    DrawWaveform(clip, EditorGUILayout.GetControlRect(false, 48));
                }
            }

            // ── Mesh ──────────────────────────────────────────────────────────────

            var modelImporter = AssetImporter.GetAtPath(path) as ModelImporter;
            if (modelImporter != null)
            {
                var mesh = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault();
                if (mesh != null)
                {
                    DrawRow("Vertices", mesh.vertexCount.ToString("N0"));
                    DrawRow("Triangles", (mesh.triangles.Length / 3).ToString("N0"));
                    DrawRow("Sub meshes", mesh.subMeshCount.ToString());
                    DrawRow("Normals", mesh.normals.Length > 0 ? "Yes" : "No");
                    DrawRow("UVs", mesh.uv?.Length > 0 ? "Yes" : "No");
                }
            }

            // ── Prefab ────────────────────────────────────────────────────────────

            if (ext == ".prefab")
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null)
                {
                    DrawRow("Components", go.GetComponentsInChildren<Component>(true).Length.ToString());
                    DrawRow("Children", go.transform.childCount.ToString());

                    var preview = AssetPreview.GetAssetPreview(go);
                    if (preview != null)
                    {
                        GUILayout.Space(4);
                        GUI.DrawTexture(EditorGUILayout.GetControlRect(false, 80), preview, ScaleMode.ScaleToFit);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        // Samples up to 1024 evenly spaced pixels and returns a match percentage
        // Returns 0–100 = similarity %, -1 = generic error, -2 = Read/Write not enabled.
        private static float ComputePixelSimilarity(Texture2D a, Texture2D b)
        {
            if (!a.isReadable || !b.isReadable)
                return -2f;

            try
            {
                Color32[] pixels_a = a.GetPixels32();
                Color32[] pixels_b = b.GetPixels32();

                int step = Mathf.Max(1, pixels_a.Length / 1024);
                int matches = 0;
                int total = 0;

                for (int i = 0; i < pixels_a.Length; i += step)
                {
                    total++;
                    if (pixels_a[i].r == pixels_b[i].r
                        && pixels_a[i].g == pixels_b[i].g
                        && pixels_a[i].b == pixels_b[i].b
                        && pixels_a[i].a == pixels_b[i].a)
                    {
                        matches++;
                    }
                }

                return total == 0 ? 0f : 100f * matches / total;
            }
            catch
            {
                return -1f;
            }
        }

        // Draws amplitude waveform bars.
        // AudioClip.GetData only works when the clip is loaded as DecompressOnLoad.
        // We temporarily switch the import setting, reload the clip, read the data,
        // then revert — all within the Editor, no runtime impact.
        private static void DrawWaveform(AudioClip clip, Rect r)
        {
            EditorGUI.DrawRect(r, Pal.BgPanel);

            float[] data = GetAudioSamples(AssetDatabase.GetAssetPath(clip));
            bool hasData = data != null && data.Length > 0;

            if (!hasData)
            {
                var fallbackStyle = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = Pal.TextDim } };
                GUI.Label(r, "  Waveform unavailable", fallbackStyle);
                return;
            }

            int bars = Mathf.Min((int)r.width, 128);
            float barW = r.width / bars;
            int step = Mathf.Max(1, data.Length / bars);

            for (int i = 0; i < bars; i++)
            {
                float peak = 0f;

                for (int j = 0; j < step && i * step + j < data.Length; j++)
                    peak = Mathf.Max(peak, Mathf.Abs(data[i * step + j]));

                float height = peak * r.height * 0.5f;
                float centerX = r.x + i * barW;
                float centerY = r.y + r.height * 0.5f;

                EditorGUI.DrawRect(
                    new Rect(centerX, centerY - height, Mathf.Max(1f, barW - 1f), height * 2f),
                    Pal.Cyan);
            }
        }

        // Reads PCM samples from an audio asset for waveform rendering.
        //
        // AudioClip.GetData requires the clip to be fully loaded in memory.
        // In the Editor, even DecompressOnLoad clips may not be loaded until
        // LoadAudioData() is called explicitly. Steps:
        //   1. If load type is not DecompressOnLoad, temporarily switch it and reimport.
        //   2. Load the clip asset fresh after any reimport.
        //   3. Call LoadAudioData() to force the audio data into memory.
        //   4. Call GetData().
        //   5. Unload and revert import settings.
        private static float[] GetAudioSamples(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null) return null;

            AudioClipLoadType originalLoadType = importer.defaultSampleSettings.loadType;
            bool needsReimport = originalLoadType != AudioClipLoadType.DecompressOnLoad;

            try
            {
                if (needsReimport)
                {
                    var settings = importer.defaultSampleSettings;
                    settings.loadType = AudioClipLoadType.DecompressOnLoad;
                    importer.defaultSampleSettings = settings;
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }

                // Always load fresh after any potential reimport
                var readClip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (readClip == null) return null;

                // Force audio data into memory — required in Editor even for DecompressOnLoad
                readClip.LoadAudioData();

                int sampleCount = readClip.samples * readClip.channels;
                if (sampleCount <= 0) return null;

                var data = new float[Mathf.Min(sampleCount, 65536)];
                bool ok = readClip.GetData(data, 0);

                // Unload to free memory before we revert
                readClip.UnloadAudioData();

                return ok ? data : null;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ADR] Waveform read failed for {path}: {ex.Message}");
                return null;
            }
            finally
            {
                if (needsReimport)
                {
                    var settings = importer.defaultSampleSettings;
                    settings.loadType = originalLoadType;
                    importer.defaultSampleSettings = settings;
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
            }
        }

        private void DrawRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label + ":", EditorStyles.miniLabel, GUILayout.Width(106));
            GUILayout.Label(value, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();
        }

        private static string FormatBytes(long b)
        {
            if (b >= 1024 * 1024) return $"{b / (1024f * 1024f):F1} MB";
            if (b >= 1024) return $"{b / 1024f:F1} KB";
            return $"{b} B";
        }
    }
}
#endif