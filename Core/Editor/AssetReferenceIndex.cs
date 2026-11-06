using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace BIEngine.UniXD.Editor {
    // One lazy reverse-dependency scan per import batch, then notification-driven updates.
    internal sealed class AssetReferenceIndex {
        private readonly Dictionary<string, string[]> m_Documents = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> m_Owners = new(StringComparer.OrdinalIgnoreCase);
        private long m_Revision = -1;
        private bool m_Ready;
        private void Refresh(string path) {
            if (m_Documents.TryGetValue(path, out var old))
                foreach (var dependency in old) m_Owners[dependency].Remove(path);
            m_Documents.Remove(path);
            if (!File.Exists(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) return;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".prefab" or ".unity" or ".asset" or ".mat" or ".controller" or ".overridecontroller" or ".anim" or ".playable")) return;
            var dependencies = AssetDatabase.GetDependencies(path, false).Where(p => p != path).ToArray();
            m_Documents[path] = dependencies;
            foreach (var dependency in dependencies) {
                if (!m_Owners.TryGetValue(dependency, out var owners)) m_Owners[dependency] = owners = new(StringComparer.OrdinalIgnoreCase);
                owners.Add(path);
            }
        }
        internal HashSet<string> Retained(string[] candidates, string directory, string prefab, string meta) {
            if (!m_Ready) {
                foreach (var path in AssetDatabase.GetAllAssetPaths()) Refresh(path);
                m_Ready = true;
            } else foreach (var path in AssetReferenceChanges.Since(m_Revision)) Refresh(path);
            // Saving generated prefabs need not have delivered an import callback yet.
            Refresh(prefab); Refresh(meta);
            m_Revision = AssetReferenceChanges.revision;
            return new HashSet<string>(candidates.Where(path => m_Owners.TryGetValue(path, out var owners)
                && owners.Any(owner => owner != prefab && owner != meta
                    && !owner.StartsWith(directory.TrimEnd('/', '\\') + "/", StringComparison.OrdinalIgnoreCase))), StringComparer.OrdinalIgnoreCase);
        }
    }
    internal sealed class AssetReferenceChanges : AssetPostprocessor {
        private static readonly Dictionary<string, long> m_Changes = new(StringComparer.OrdinalIgnoreCase);
        internal static long revision { get; private set; }
        internal static string[] Since(long revision) => m_Changes.Where(p => p.Value > revision).Select(p => p.Key).ToArray();
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) {
            foreach (var path in imported.Concat(deleted).Concat(moved).Concat(movedFrom)) m_Changes[path] = ++revision;
        }
    }
}
