using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BIEngine.UniXD.Editor
{
    // Disposable, project-local optimization data. Never referenced by import settings.
    public static class ImportContentCache
    {
        [Serializable] private sealed class Entry { public string key, stamp, hash; }
        [Serializable] private sealed class Data { public int version = 1; public List<Entry> entries = new List<Entry>(); }
        private const string m_Path = "Library/UniXD/ContentCache.json";
        private static Dictionary<string, Entry> m_Entries;
        private static bool m_Dirty;
        internal static int fileHashComputations { get; private set; }
        internal static int payloadHashComputations { get; private set; }

        public static string FileStamp(string path)
        {
            var full = Path.GetFullPath(path);
            var file = new FileInfo(full);
            if (!file.Exists) return "missing";
            var project = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath) ?? ".").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var relative = full.StartsWith(project, StringComparison.OrdinalIgnoreCase) ? full.Substring(project.Length).Replace('\\', '/') : "";
            var dependency = relative.StartsWith("Assets/", StringComparison.Ordinal) || relative.StartsWith("Packages/", StringComparison.Ordinal)
                ? AssetDatabase.GetAssetDependencyHash(relative).ToString() : "external";
            return file.Length + ":" + file.LastWriteTimeUtc.Ticks + "|" + dependency;
        }

        public static string GetFileHash(string path)
        {
            var key = "file:" + Path.GetFullPath(path);
            var stamp = FileStamp(path);
            // External documents have no Unity import revision. Verify their bytes
            // once per batch rather than treating timestamps alone as proof.
            var external = stamp.EndsWith("|external", StringComparison.Ordinal);
            if (!external && TryGet(key, stamp, out var cached)) return cached;
            if (external && m_ExternalChecked.Contains(key) && TryGet(key, stamp, out cached)) return cached;
            string hash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path)) hash = Convert.ToBase64String(sha.ComputeHash(stream));
            fileHashComputations++;
            Remember(key, stamp, hash);
            if (external) m_ExternalChecked.Add(key);
            return hash;
        }

        private static readonly HashSet<string> m_ExternalChecked = new HashSet<string>(StringComparer.Ordinal);

        public static string GetPayloadHash(string key, string sourceRevision, Func<byte[]> load)
        {
            key = "payload:" + key;
            if (TryGet(key, sourceRevision, out var cached)) return cached;
            using (var sha = SHA256.Create()) cached = Convert.ToBase64String(sha.ComputeHash(load()));
            payloadHashComputations++;
            Remember(key, sourceRevision, cached);
            return cached;
        }

        internal static bool ArtifactCurrent(string path, string signature) => File.Exists(path)
            && TryGet("artifact:" + Path.GetFullPath(path), FileStamp(path), out var previous) && previous == signature;

        internal static bool ArtifactOutputCurrent(string path) => File.Exists(path)
            && TryGet("artifact:" + Path.GetFullPath(path), FileStamp(path), out _);

        internal static void RememberArtifact(string path, string signature) => Remember("artifact:" + Path.GetFullPath(path), FileStamp(path), signature);

        private static bool TryGet(string key, string stamp, out string hash)
        {
            Ensure();
            hash = null;
            if (!m_Entries.TryGetValue(key, out var entry) || entry.stamp != stamp || string.IsNullOrEmpty(entry.hash)) return false;
            hash = entry.hash;
            return true;
        }

        private static void Remember(string key, string stamp, string hash)
        {
            Ensure();
            if (m_Entries.TryGetValue(key, out var old) && old.stamp == stamp && old.hash == hash) return;
            m_Entries[key] = new Entry { key = key, stamp = stamp, hash = hash };
            m_Dirty = true;
        }

        private static void Ensure()
        {
            if (m_Entries != null) return;
            m_Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(m_Path)) return;
                var data = JsonUtility.FromJson<Data>(File.ReadAllText(m_Path));
                if (data?.version != 1 || data.entries == null) return;
                foreach (var entry in data.entries)
                    if (entry != null && !string.IsNullOrEmpty(entry.key)) m_Entries[entry.key] = entry;
            }
            catch (Exception error) when (error is IOException || error is ArgumentException) { m_Entries.Clear(); }
        }

        public static void Flush()
        {
            m_ExternalChecked.Clear();
            if (!m_Dirty) return;
            var temporary = m_Path + ".tmp";
            try
            {
                Directory.CreateDirectory("Library/UniXD");
                var text = JsonUtility.ToJson(new Data { entries = m_Entries.Values.OrderBy(e => e.key, StringComparer.Ordinal).ToList() });
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(m_Path)) File.Replace(temporary, m_Path, null);
                        else File.Move(temporary, m_Path);
                        break;
                    }
                    catch (IOException) when (attempt < 7 && File.Exists(temporary))
                    { System.Threading.Thread.Sleep(Math.Min(20 << attempt, 200)); }
                }
                m_Dirty = false;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // Losing an optimization cache must not invalidate saved prefabs.
                Debug.LogWarning("UniXD 内容缓存暂未写入，将在下次导出重试: " + error.Message);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static class ImportAtlasCache
    {
        internal static string Signature(UnityEngine.Object[] assets, UnityEngine.Object preset, int area,
            TextureImporterCompression compression, int quality)
        {
            var entries = new List<string> { "Atlas-v1", Application.unityVersion, EditorUserBuildSettings.activeBuildTarget.ToString(),
                area.ToString(), compression.ToString(), quality.ToString(), ImportFingerprintSession.ObjectFingerprint(preset) };
            foreach (var asset in assets.Where(a => a != null))
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id);
                entries.Add(guid + ":" + id + ":" + AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(asset)));
            }
            entries.Sort(StringComparer.Ordinal);
            return ImportFingerprintSession.Hash(Encoding.UTF8.GetBytes(string.Join("\n", entries)));
        }
    }
}
