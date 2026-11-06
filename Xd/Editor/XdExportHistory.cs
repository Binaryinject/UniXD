using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BIEngine.UniXD.Loader;
using UnityEditor;
using UnityEngine;

namespace BIEngine.UniXD.Xd
{
    internal enum XdScrollViewMode
    {
        None,
        Standard,
        Osa,
    }

    internal static class XdFlattenHistory
    {
        internal static string Capture(string file, string board) => JsonUtility.ToJson(Read(file, board));

        internal static void Restore(string file, string board, string json)
        {
            EditorPrefs.SetString(Key(file, board), ValidateArchive(json));
        }

        internal static string ValidateArchive(string json)
        {
            var archive = JsonUtility.FromJson<Archive>(json);
            if (archive == null) throw new ArgumentException("无效的 XD 图层配置。");
            return JsonUtility.ToJson(archive);
        }

        [Serializable] private sealed class Rename { public string id, name; }
        [Serializable] private sealed class Archive { public string[] ids; public Rename[] names; public string[] excluded; public string[] omitted; public string[] ordinaryScrollGroups; public string[] osaScrollGroups; public string[] vectorLayers; public string[] mergedShadowLayers; }
        private static string Key(string file, string board) => "AkyuiXd.Flatten.v1." + BIEngine.UniXD.Editor.ImportFingerprintSession.Hash(
            System.Text.Encoding.UTF8.GetBytes(Application.dataPath.ToLowerInvariant() + "\n" + Path.GetFullPath(file).Replace('\\', '/').ToLowerInvariant() + "\n" + board));
        public static HashSet<string> Load(string file, string board)
        {
            try { return new HashSet<string>(JsonUtility.FromJson<Archive>(EditorPrefs.GetString(Key(file, board), "{}"))?.ids ?? Array.Empty<string>()); }
            catch (ArgumentException) { return new HashSet<string>(); }
        }
        private static Archive Read(string file, string board)
        {
            try { return JsonUtility.FromJson<Archive>(EditorPrefs.GetString(Key(file, board), "{}")) ?? new Archive(); }
            catch (ArgumentException) { return new Archive(); }
        }
        public static void Save(string file, string board, IEnumerable<string> ids)
        {
            var archive = Read(file, board); archive.ids = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            EditorPrefs.SetString(Key(file, board), JsonUtility.ToJson(archive));
        }
        public static HashSet<string> LoadExcluded(string file, string board) => new HashSet<string>(Read(file, board).excluded ?? Array.Empty<string>());
        public static HashSet<string> LoadOmitted(string file, string board) => new HashSet<string>(Read(file, board).omitted ?? Array.Empty<string>());
        public static HashSet<string> LoadMergedShadows(string file, string board) => new HashSet<string>(Read(file, board).mergedShadowLayers ?? Array.Empty<string>());
        public static void SaveMergedShadows(string file, string board, IEnumerable<string> ids)
        {
            var archive = Read(file, board);
            archive.mergedShadowLayers = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            EditorPrefs.SetString(Key(file, board), JsonUtility.ToJson(archive));
        }
        public static HashSet<string> LoadVectors(string file, string board) => new HashSet<string>(Read(file, board).vectorLayers ?? Array.Empty<string>());
        public static void SaveVectors(string file, string board, IEnumerable<string> ids)
        {
            var archive = Read(file, board);
            archive.vectorLayers = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            EditorPrefs.SetString(Key(file, board), JsonUtility.ToJson(archive));
        }
        public static Dictionary<string, XdScrollViewMode> LoadScrollViewModes(string file, string board)
        {
            var archive = Read(file, board);
            var modes = new Dictionary<string, XdScrollViewMode>();
            foreach (var id in archive.ordinaryScrollGroups ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(id)) modes[id] = XdScrollViewMode.Standard;
            foreach (var id in archive.osaScrollGroups ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(id)) modes[id] = XdScrollViewMode.Osa;
            return modes;
        }
        public static void SaveScrollViewModes(string file, string board, IReadOnlyDictionary<string, XdScrollViewMode> modes)
        {
            var archive = Read(file, board);
            archive.ordinaryScrollGroups = modes.Where(x => x.Value == XdScrollViewMode.Standard).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            archive.osaScrollGroups = modes.Where(x => x.Value == XdScrollViewMode.Osa).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            EditorPrefs.SetString(Key(file, board), JsonUtility.ToJson(archive));
        }
        public static void SaveOmitted(string file, string board, IEnumerable<string> ids)
        {
            var archive = Read(file, board); archive.omitted = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            EditorPrefs.SetString(Key(file, board), JsonUtility.ToJson(archive));
        }
        public static void SaveExcluded(string file, string board, IEnumerable<string> ids)
        {
            var archive = Read(file, board); archive.excluded = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            EditorPrefs.SetString(Key(file, board), JsonUtility.ToJson(archive));
        }
        public static Dictionary<string, string> LoadNames(string file, string board) => (Read(file, board).names ?? Array.Empty<Rename>())
            .Where(x => x != null && !string.IsNullOrEmpty(x.id) && !string.IsNullOrWhiteSpace(x.name))
            .GroupBy(x => x.id).ToDictionary(x => x.Key, x => x.Last().name);
        public static void SaveNames(string file, string board, IReadOnlyDictionary<string, string> names)
        {
            var archive = Read(file, board);
            archive.names = names.Where(x => !string.IsNullOrWhiteSpace(x.Value)).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new Rename { id = x.Key, name = x.Value.Trim() }).ToArray();
            EditorPrefs.SetString(Key(file, board), JsonUtility.ToJson(archive));
        }
    }

    internal static class XdArtboardExportNameHistory
    {
        [Serializable]
        private sealed class Record
        {
            public string manifestPath;
            public string exportName;
        }

        [Serializable]
        private sealed class Archive
        {
            public Record[] records;
        }

        public static Dictionary<string, string> Load(string xdFilePath)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var json = EditorPrefs.GetString(GetKey(xdFilePath), "");
            if (string.IsNullOrEmpty(json)) return result;
            try
            {
                var archive = JsonUtility.FromJson<Archive>(json);
                foreach (var record in archive?.records ?? Array.Empty<Record>())
                    if (record != null && !string.IsNullOrWhiteSpace(record.manifestPath) && !string.IsNullOrWhiteSpace(record.exportName))
                        result[record.manifestPath] = record.exportName.Trim();
            }
            catch (ArgumentException) { /* Invalid local preferences are treated as an empty archive. */ }
            return result;
        }

        public static void Save(string xdFilePath, IReadOnlyDictionary<string, string> names)
        {
            var archive = new Archive { records = names.Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new Record { manifestPath = x.Key, exportName = x.Value.Trim() }).ToArray() };
            EditorPrefs.SetString(GetKey(xdFilePath), JsonUtility.ToJson(archive));
        }

        private static string GetKey(string xdFilePath)
        {
            // Match absolute/relative paths and Windows path casing, while isolating projects.
            var identity = Path.GetFullPath(Application.dataPath).Replace('\\', '/').ToLowerInvariant() + "\n" +
                Path.GetFullPath(xdFilePath).Replace('\\', '/').ToLowerInvariant();
            return "AkyuiXd.ExportNames.v1." + BIEngine.UniXD.Editor.ImportFingerprintSession.Hash(System.Text.Encoding.UTF8.GetBytes(identity));
        }
    }

    internal static class XdArtboardSelectionHistory
    {
        private const string KeyPrefix = "AkyuiXd.ArtboardSelection.{name}.";

        public static string[] Load(string xdFilePath)
        {
            var value = EditorPrefs.GetString(GetKey(xdFilePath), string.Empty);
            return value.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        }

        public static void Save(string xdFilePath, IEnumerable<string> selectedArtboardPaths)
        {
            EditorPrefs.SetString(GetKey(xdFilePath), string.Join("\n", selectedArtboardPaths));
        }

        private static string GetKey(string xdFilePath)
        {
            return KeyPrefix.Replace("{name}", PlayerSettings.productName) + FastHash.CalculateHash(xdFilePath);
        }
    }
}
