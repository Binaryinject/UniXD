using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Loader;
using UnityEditor;
using UnityEditor.Presets;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.CommonTrigger
{
    // All sharing policy, indexing and migration live in this optional import plugin.
    [CreateAssetMenu(menuName = "UniXD/Triggers/SharedSpriteResource", fileName = nameof(SharedSpriteResourceTrigger))]
    public sealed class SharedSpriteResourceTrigger : ImportTrigger
    {
        [SerializeField] private string[] globalPrimaryLibraryNames = { "Resource" };
        [SerializeField, Tooltip("留空时使用资源目录模板中的 {name} = Shared。")]
        private string m_SharedDirectory = "";
        [SerializeField, Min(0)] private int m_MaxSpriteArea = SpriteAtlasAreaFilter.defaultMaxSpriteArea;
        [SerializeField] private TextureImporterCompression m_StandaloneCompression = TextureImporterCompression.Compressed;
        [SerializeField, Range(0, 100)] private int m_CompressionQuality = 50;
        [SerializeField] private Preset m_AtlasPreset;

        private SharedSpriteIndex m_Index;
        private IImportSettings m_Settings;
        private string m_Root;
        private string m_Shared;
        private string m_IndexPath;
        private long m_Revision = -1;
        private readonly Dictionary<string, string> m_Aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_SourceKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_ManagedAtlases = new HashSet<string>(StringComparer.Ordinal);
        private bool m_AtlasDirty;
        private bool m_Ready;
        private string m_Policy;

        public override void OnPreprocessAllAssets(ILoader loader, ref List<IAsset> assets)
        {
            EnsureIndex(Importer.Settings);
            RefreshIndex();
            RecoverMigrations();
            m_Aliases.Clear();
            m_SourceKeys.Clear();
            if (IsPrimaryPanel(loader.LayoutInfo.Name)) return;
            foreach (var asset in assets.OfType<SpriteAsset>())
            {
                var fingerprint = Importer.Fingerprints?.GetFingerprint(asset);
                if (string.IsNullOrEmpty(fingerprint)) continue;
                m_SourceKeys[asset.FileName] = fingerprint;
                if (Importer.Settings.ReimportAsset || !m_Index.bindings.TryGetValue(fingerprint, out var binding)) continue;
                if (!m_Index.images.TryGetValue(binding.guid, out var image)) continue;
                RefreshImage(image.path);
                if (!m_Index.images.TryGetValue(binding.guid, out image) || image.key != binding.key) continue;
                if (Priority(image.path) > 1 || !File.Exists(image.path)) continue;
                m_Aliases[asset.FileName] = image.path;
            }
            assets.RemoveAll(a => m_Aliases.ContainsKey(a.FileName));
        }

        public override string OnLoadAsset(string fileName) => m_Aliases.TryGetValue(fileName, out var path) ? path : null;

        public override void OnImportCompleted(ILoader loader, IPathGetter paths)
        {
            EnsureIndex(Importer.Settings ?? m_Settings);
            RefreshIndex();
            // Saved files may not have produced a postprocessor callback yet.
            m_Index.RefreshDocument(paths.PrefabSavePath);
            m_Index.RefreshDocument(paths.MetaSavePath);
            foreach (var path in Directory.GetFiles(paths.AssetOutputDirectoryPath, "*", SearchOption.AllDirectories))
                RefreshPath(path.Replace('\\', '/'));
            foreach (var source in m_SourceKeys)
            {
                var resolved = m_Aliases.TryGetValue(source.Key, out var alias) ? alias
                    : paths.AssetOutputDirectoryPath.TrimEnd('/') + "/" + source.Key;
                var guid = AssetDatabase.AssetPathToGUID(resolved);
                if (m_Index.images.TryGetValue(guid, out var image))
                    m_Index.Bind(loader.LayoutInfo.Name + "/" + source.Key, source.Value, image);
            }
            Consolidate();
        }

        public override void OnImportBatchCompleted(IImportSettings settings, bool succeeded)
        {
            if (m_Index == null || !m_Ready) return;
            // Earlier successful panels remain valid if a later panel was cancelled.
            try
            {
                DiscoverSharedFiles();
                RefreshIndex();
                Consolidate();
                PruneUnusedShared();
                if (m_AtlasDirty) RebuildSharedAtlases();
                SaveIndex();
                AssetDatabase.SaveAssets();
            }
            finally { m_Aliases.Clear(); m_SourceKeys.Clear(); }
        }

        private void EnsureIndex(IImportSettings settings)
        {
            if (settings == null) throw new InvalidOperationException("SharedSpriteResource requires an active import configuration.");
            var template = settings.AssetOutputDirectoryPath.Replace('\\', '/');
            var token = template.IndexOf("{name}", StringComparison.Ordinal);
            if (token < 0) throw new InvalidOperationException("SharedSpriteResource 的资源目录必须包含 {name}，以区分面板资源所有权。");
            var root = template.Substring(0, token).TrimEnd('/');
            var shared = string.IsNullOrWhiteSpace(m_SharedDirectory) ? template.Replace("{name}", "Shared").TrimEnd('/') : m_SharedDirectory.Replace('\\', '/').TrimEnd('/');
            if (!root.StartsWith("Assets/", StringComparison.Ordinal) || !shared.StartsWith(root + "/", StringComparison.Ordinal)
                || shared.Contains("..") || root.Contains(".."))
                throw new InvalidOperationException("Shared 目录必须位于配置的 Assets 资源根目录内。");
            m_Settings = settings;
            var policy = "AtlasState-v2:" + string.Join("|", globalPrimaryLibraryNames ?? Array.Empty<string>()) + ":" + m_MaxSpriteArea + ":" + m_StandaloneCompression + ":" + m_CompressionQuality + ":" + ImportFingerprintSession.ObjectFingerprint(m_AtlasPreset) + ":" + EditorUserBuildSettings.activeBuildTarget;
            if (m_Index != null && m_Policy != policy)
            {
                foreach (var image in m_Index.images.Values) m_Index.changedKeys.Add(image.key);
                m_AtlasDirty = true;
            }
            m_Policy = policy;
            if (m_Ready && m_Index != null && m_Root == root && m_Shared == shared) return;
            m_Ready = false;
            m_Root = root;
            m_Shared = shared;
            m_IndexPath = shared + "/.akyui-shared-index.json";
            m_Index = SharedSpriteIndex.Load(m_IndexPath);
            m_ManagedAtlases.Clear();
            foreach (var path in Directory.EnumerateFiles(root, "*.spriteatlasv2", SearchOption.AllDirectories))
                m_ManagedAtlases.Add(path.Replace('\\', '/'));
            // Repair old scalar empty lists and atlas ownership before loading any
            // sprites: Unity logs duplicate-atlas warnings during asset loading too.
            RepairAtlasOwnership();
            EditorUtil.CreateDirectory(shared);
            // Enumerate metadata once per editor session; decoding is stamp cached.
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { root })) RefreshImage(AssetDatabase.GUIDToAssetPath(guid));
            DiscoverSharedFiles();
            m_Index.RemoveMissingImages();
            m_Index.ScanDocuments();
            m_Index.ApplyKeyUpgrades();
            m_Revision = SharedSpriteChanges.revision;
            m_AtlasDirty |= m_Index.atlasPolicy != m_Policy || settings.ReimportAsset || m_Index.atlasPaths.Any(p => !ImportContentCache.ArtifactOutputCurrent(p));
            m_Ready = true;
        }

        private bool IsPrimaryPanel(string panel) => (globalPrimaryLibraryNames ?? Array.Empty<string>()).Contains(panel, StringComparer.OrdinalIgnoreCase);

        private void DiscoverSharedFiles()
        {
            if (!Directory.Exists(m_Shared)) return;
            // The registry can be missing a physical file after a cancelled or
            // deferred Unity delete. Reconcile only unknown Shared files; known
            // images keep their revision/hash fast path.
            foreach (var file in Directory.EnumerateFiles(m_Shared, "*.png", SearchOption.AllDirectories))
            {
                var path = file.Replace('\\', '/');
                if (m_Index.FindPath(path) != null) continue;
                if (AssetImporter.GetAtPath(path) == null)
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                RefreshImage(path);
            }
        }
        private string Panel(string path) => path.StartsWith(m_Root + "/", StringComparison.Ordinal) ? path.Substring(m_Root.Length + 1).Split('/')[0] : "";
        private int Priority(string path) => IsPrimaryPanel(Panel(path)) ? 0 : path.StartsWith(m_Shared + "/", StringComparison.Ordinal) ? 1 : 2;

        private void RefreshIndex()
        {
            foreach (var path in SharedSpriteChanges.Since(m_Revision)) RefreshPath(path);
            m_Index.ApplyKeyUpgrades();
            m_Revision = SharedSpriteChanges.revision;
        }

        private void RefreshPath(string path)
        {
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            { m_Index.RefreshDocument(path); path = path.Substring(0, path.Length - 5); }
            else m_Index.RefreshDocument(path + ".meta");
            if (path.EndsWith(".spriteatlasv2", StringComparison.OrdinalIgnoreCase) && path.StartsWith(m_Root + "/", StringComparison.Ordinal))
            {
                if (File.Exists(path)) m_ManagedAtlases.Add(path);
                else m_ManagedAtlases.Remove(path);
                if (path.StartsWith(m_Shared + "/", StringComparison.Ordinal) && !ImportContentCache.ArtifactOutputCurrent(path)) m_AtlasDirty = true;
            }
            if (path.StartsWith(m_Root + "/", StringComparison.Ordinal))
            {
                if (Directory.Exists(path))
                    foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { path })) RefreshImage(AssetDatabase.GUIDToAssetPath(guid));
                else if (File.Exists(path)) RefreshImage(path);
                else
                {
                    m_Index.RemoveImageAt(path);
                    foreach (var removed in m_Index.images.Values.Where(i => i.path.StartsWith(path + "/", StringComparison.Ordinal)).Select(i => i.path).ToArray())
                        m_Index.RemoveImageAt(removed);
                }
            }
            m_Index.RefreshDocument(path);
        }

        private void RefreshImage(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer) || importer.textureType != TextureImporterType.Sprite
                || importer.spriteImportMode != SpriteImportMode.Single || !File.Exists(path))
            { m_Index.RemoveImageAt(path); return; }
            var guid = AssetDatabase.AssetPathToGUID(path);
            var revision = ImportContentCache.FileStamp(path) + "|" + EditorUserBuildSettings.activeBuildTarget + "|SharedPixels-v2";
            m_Index.images.TryGetValue(guid, out var old);
            if (old != null && old.revisionStamp == revision) { old.path = path; return; }
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            var importedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var sourceSizing = "";
            if (importer.spriteBorder != Vector4.zero)
            {
                sourceSizing = "unknown:" + guid;
                try
                {
                    var metadata = Utf8Json.JsonSerializer.Deserialize<Dictionary<string, object>>(importer.userData);
                    if (metadata != null && metadata.TryGetValue("source_width", out var w) && metadata.TryGetValue("source_height", out var h))
                        sourceSizing = Convert.ToString(w, CultureInfo.InvariantCulture) + "x" + Convert.ToString(h, CultureInfo.InvariantCulture);
                }
                catch (Exception error) when (!(error is OutOfMemoryException)) { }
            }
            // Import scaling is output policy. Equal source pixels can be reused
            // at different Max Texture Sizes; primary libraries still win.
            var semantics = string.Join("|", importer.spriteBorder.ToString("R", CultureInfo.InvariantCulture), importer.spritePixelsPerUnit.ToString("R", CultureInfo.InvariantCulture),
                settings.spritePivot.ToString("R", CultureInfo.InvariantCulture), settings.spriteAlignment, settings.spriteMeshType, settings.spriteExtrude, importer.sRGBTexture,
                importer.alphaIsTransparency, importer.mipmapEnabled, importer.filterMode, importer.wrapMode, importer.npotScale, sourceSizing);
            var rawHash = ImportContentCache.GetFileHash(path);
            var previousFile = old ?? m_Index.FindPath(path);
            var sameBytes = previousFile != null && (previousFile.encoded ?? "").Split('|')[0] == rawHash ? previousFile : m_Index.FindEncoded(rawHash);
            string pixelHash = sameBytes?.pixelHash;
            int width = sameBytes?.sourceWidth ?? 0, height = sameBytes?.sourceHeight ?? 0;
            // Upgrade old indices without decoding the same PNG again: the current
            // file's strong digest has already verified the old pixel digest.
            if (sameBytes != null && string.IsNullOrEmpty(pixelHash))
            {
                var parts = (sameBytes.key ?? "").Split('|');
                var dimensions = parts.Length > 1 ? parts[1].Split('x') : Array.Empty<string>();
                if (dimensions.Length == 2 && int.TryParse(dimensions[0], out width) && int.TryParse(dimensions[1], out height)) pixelHash = parts[0];
            }
            if (string.IsNullOrEmpty(pixelHash) || width <= 0 || height <= 0)
            {
                Importer.CurrentProgress?.Update("更新共享图片索引 " + Path.GetFileName(path), .90f);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path))) throw new InvalidDataException("Cannot decode " + path);
                    width = texture.width; height = texture.height;
                    var pixels = texture.GetPixels32();
                    var rgba = new byte[pixels.Length * 4];
                    for (var i = 0; i < pixels.Length; i++)
                    { rgba[i * 4] = pixels[i].r; rgba[i * 4 + 1] = pixels[i].g; rgba[i * 4 + 2] = pixels[i].b; rgba[i * 4 + 3] = pixels[i].a; }
                    pixelHash = SharedSpriteIndex.Hash(rgba);
                    pixelHashComputations++;
                }
                finally { Object.DestroyImmediate(texture); }
            }
            var key = pixelHash + "|" + width + "x" + height + "|" + semantics;
            var image = new SharedSpriteIndex.Image { guid = guid, path = path, revisionStamp = revision, encoded = rawHash, key = key,
                pixelHash = pixelHash, sourceWidth = width, sourceHeight = height,
                width = importedTexture != null ? importedTexture.width : width, height = importedTexture != null ? importedTexture.height : height };
            m_Index.SetImage(image);
            if (old == null || old.key != key)
            {
                m_Index.changedKeys.Add(key);
                if (old != null)
                {
                    m_Index.changedKeys.Add(old.key);
                    var legacy = old.key.Split('|');
                    if (legacy.Length == 18 && string.Join("|", legacy.Take(14).Concat(new[] { legacy[17] })) == key)
                        m_Index.keyUpgrades[old.key] = key;
                }
                if (Priority(path) < 2) m_AtlasDirty = true;
            }
            else if (Priority(path) < 2 && (old.width != image.width || old.height != image.height)) m_AtlasDirty = true;
        }

        internal int pixelHashComputations { get; private set; }

        private void Consolidate()
        {
            var keys = m_Index.changedKeys.ToArray();
            m_Index.changedKeys.Clear();
            foreach (var key in keys)
            {
                var group = m_Index.Group(key).Where(i => File.Exists(i.path))
                    .OrderBy(i => Priority(i.path)).ThenBy(i => i.order).ThenBy(i => i.guid, StringComparer.Ordinal).ToArray();
                if (group.Length == 0) continue;
                var canonical = group[0];
                if (Priority(canonical.path) == 2 && group.Select(i => Panel(i.path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                {
                    var previousPath = canonical.path;
                    var target = m_Shared + "/" + canonical.guid + Path.GetExtension(canonical.path);
                    var error = AssetDatabase.MoveAsset(canonical.path, target);
                    if (!string.IsNullOrEmpty(error)) throw new IOException(error);
                    canonical.path = target; // GUID/meta retained: original panel references survive.
                    m_Index.UpdateImagePath(canonical, previousPath);
                    m_Index.RefreshDocument(previousPath + ".meta");
                    m_Index.RefreshDocument(target + ".meta");
                    m_AtlasDirty = true;
                }
                foreach (var duplicate in group.Skip(1))
                {
                    if (Priority(duplicate.path) == 0) continue; // Never delete artists' primary assets.
                    var migration = new SharedSpriteIndex.Migration { source = duplicate.guid, target = canonical.guid };
                    m_Index.pending.Add(migration);
                }
            }
            RecoverMigrations();
        }

        private void RecoverMigrations()
        {
            if (m_Index.pending.Count == 0) return;
            m_Index.SaveJournal(m_IndexPath);
            // An atlas must release a duplicate rather than replace it with the
            // canonical sprite, which already belongs to its own atlas.
            RepairAtlasOwnership(new HashSet<string>(m_Index.pending.Select(p => p.source), StringComparer.Ordinal));
            var replacements = new Dictionary<string, SharedSpriteIndex.Replacement>(StringComparer.Ordinal);
            var objectMap = new Dictionary<Object, Object>();
            foreach (var migration in m_Index.pending)
            {
                var sourcePath = AssetDatabase.GUIDToAssetPath(migration.source);
                var targetPath = AssetDatabase.GUIDToAssetPath(migration.target);
                if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
                    throw new IOException("Shared 迁移目标缺失，保留原资源: " + migration.target);
                if (string.IsNullOrEmpty(sourcePath)) continue;
                var sourceObjects = AssetDatabase.LoadAllAssetsAtPath(sourcePath).Where(o => o != null).ToArray();
                var targetObjects = AssetDatabase.LoadAllAssetsAtPath(targetPath).Where(o => o != null).ToArray();
                var replacement = new SharedSpriteIndex.Replacement { guid = migration.target };
                foreach (var source in sourceObjects)
                {
                    var target = targetObjects.SingleOrDefault(o => o.GetType() == source.GetType());
                    if (target == null) throw new InvalidOperationException("Shared 迁移不支持该子资源类型: " + sourcePath);
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string _, out long sourceId);
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out string _, out long targetId);
                    replacement.ids[sourceId] = targetId; objectMap[source] = target;
                }
                replacements[migration.source] = replacement;
            }
            // Rewrite/import each affected prefab, metadata file or atlas once per batch,
            // even when hundreds of its sprites have become shared together.
            var affected = new HashSet<string>(replacements.Keys.SelectMany(m_Index.Owners), StringComparer.Ordinal);
            foreach (var path in affected) m_Index.ReplaceReferences(path, replacements, objectMap);
            foreach (var component in Resources.FindObjectsOfTypeAll<Component>())
                if (component != null && !EditorUtility.IsPersistent(component) && component.gameObject.scene.IsValid()
                    && SharedSpriteIndex.ReplaceObjectReferences(component, objectMap, true))
                    EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
            foreach (var source in replacements.Keys)
                if (m_Index.Owners(source).Any()) throw new IOException("Shared 迁移后仍存在旧引用: " + source);
            var targets = m_Index.pending.ToDictionary(p => p.source, p => p.target, StringComparer.Ordinal);
            foreach (var binding in m_Index.bindings.Values)
                if (targets.TryGetValue(binding.guid, out var target)) { binding.guid = target; m_Index.MarkDirty(); }
            AssetDatabase.SaveAssets();
            // Reference commits precede source deletion. The journal remains durable
            // until every deletion succeeds, including interruption between deletions.
            var deleted = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var migration in m_Index.pending)
                {
                    var sourcePath = AssetDatabase.GUIDToAssetPath(migration.source);
                    if (string.IsNullOrEmpty(sourcePath) && m_Index.images.TryGetValue(migration.source, out var recorded))
                        sourcePath = recorded.path;
                    if (!string.IsNullOrEmpty(sourcePath) && (File.Exists(sourcePath) || File.Exists(sourcePath + ".meta")))
                    {
                        var currentGuid = AssetDatabase.AssetPathToGUID(sourcePath);
                        if (!string.IsNullOrEmpty(currentGuid) && currentGuid != migration.source)
                            throw new IOException("Shared duplicate path now belongs to a different asset: " + sourcePath);
                        if (!AssetDatabase.DeleteAsset(sourcePath)) throw new IOException("Cannot remove duplicate " + sourcePath);
                    }
                    if (!string.IsNullOrEmpty(sourcePath)) deleted.Add(sourcePath);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            // Unity can defer deletion while asset editing. Do not drop registry
            // entries or the durable journal before the physical files are gone.
            foreach (var path in deleted)
            {
                if (File.Exists(path) || File.Exists(path + ".meta"))
                {
                    AssetDatabase.DeleteAsset(path);
                    if (File.Exists(path) || File.Exists(path + ".meta")) throw new IOException("Shared duplicate deletion is incomplete: " + path);
                }
                m_Index.RemoveImageAt(path);
            }
            m_Index.pending.Clear();
            m_Index.SaveJournal(m_IndexPath);
            m_AtlasDirty = true;
        }

        private void RebuildSharedAtlases()
        {
            var sharedImages = m_Index.images.Values.Where(i => Priority(i.path) == 1 && File.Exists(i.path)).OrderBy(i => i.guid, StringComparer.Ordinal).ToArray();
            var batches = new List<List<SharedSpriteIndex.Image>>();
            var batch = new List<SharedSpriteIndex.Image>();
            long area = 0;
            foreach (var image in sharedImages)
            {
                var size = (long)image.width * image.height;
                if (m_MaxSpriteArea > 0 && size > m_MaxSpriteArea) continue;
                if (batch.Count > 0 && area + size > 2048L * 2048 * 2 / 3) { batches.Add(batch); batch = new List<SharedSpriteIndex.Image>(); area = 0; }
                batch.Add(image); area += size;
            }
            if (batch.Count > 0) batches.Add(batch);
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < batches.Count; i++)
                foreach (var image in batches[i]) owners[image.guid] = SharedAtlasPath(i);
            // Detach every old shard first. Saving a new shard before releasing
            // its members from old shards also creates transient duplicate owners.
            RepairAtlasOwnership(null, owners);
            var expected = new HashSet<string>(StringComparer.Ordinal);
            var all = sharedImages.Select(i => AssetDatabase.LoadAssetAtPath<Object>(i.path)).Where(o => o != null).ToArray();
            var policy = SpriteAtlasAreaFilter.PrepareTexturePolicy(all, m_MaxSpriteArea);
            policy.RestorePackedSources();
            for (var i = 0; i < batches.Count; i++)
            {
                var path = SharedAtlasPath(i);
                expected.Add(path);
                var members = batches[i].Select(image => AssetDatabase.LoadAssetAtPath<Object>(image.path)).ToArray();
                var signature = ImportAtlasCache.Signature(members, m_AtlasPreset, m_MaxSpriteArea, m_StandaloneCompression, m_CompressionQuality);
                if (m_Settings.ReimportAsset != true && ImportContentCache.ArtifactCurrent(path, signature)) continue;
                var atlas = new SpriteAtlasAsset { name = Path.GetFileNameWithoutExtension(path) };
                atlas.Add(batches[i].Select(image => AssetDatabase.LoadAssetAtPath<Object>(image.path)).ToArray());
                SpriteAtlasAsset.Save(atlas, path);
                m_ManagedAtlases.Add(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(path);
                if (m_AtlasPreset != null) { m_AtlasPreset.ApplyTo(importer); importer.SaveAndReimport(); }
                m_Index.RefreshDocument(path);
            }
            foreach (var path in Directory.GetFiles(m_Shared, "_Shared_*.spriteatlasv2").Select(p => p.Replace('\\', '/')))
                if (!expected.Contains(path)) { AssetDatabase.DeleteAsset(path); m_ManagedAtlases.Remove(path); m_Index.RefreshDocument(path); }
            policy.ApplyStandaloneCompression(m_StandaloneCompression, m_CompressionQuality);
            for (var i = 0; i < batches.Count; i++)
                ImportContentCache.RememberArtifact(SharedAtlasPath(i), ImportAtlasCache.Signature(
                    batches[i].Select(image => AssetDatabase.LoadAssetAtPath<Object>(image.path)).ToArray(),
                    m_AtlasPreset, m_MaxSpriteArea, m_StandaloneCompression, m_CompressionQuality));
            m_Index.SetAtlasPolicy(m_Policy, expected.OrderBy(p => p, StringComparer.Ordinal).ToArray());
            m_AtlasDirty = false;
        }

        private void SaveIndex() => m_Index.Save(m_IndexPath);

        private string SharedAtlasPath(int index) => m_Shared + "/_Shared_" + index.ToString("D3", CultureInfo.InvariantCulture) + ".spriteatlasv2";

        private void RepairAtlasOwnership(ISet<string> released = null, IReadOnlyDictionary<string, string> sharedOwners = null)
        {
            var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
            var changed = new List<string>();
            // This pass only removes members. Commit all removals before importing
            // prefabs, loading sprites, or adding any canonical atlas members.
            foreach (var path in m_ManagedAtlases.OrderBy(p => p, StringComparer.Ordinal).ToArray())
            {
                if (!File.Exists(path)) { m_ManagedAtlases.Remove(path); continue; }
                var text = File.ReadAllText(path);
                var updated = SharedSpriteAtlasLists.Filter(text, guid =>
                {
                    if (released != null && released.Contains(guid)) return false;
                    var imagePath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!imagePath.StartsWith(m_Root + "/", StringComparison.Ordinal)) return true;
                    var shared = imagePath.StartsWith(m_Shared + "/", StringComparison.Ordinal);
                    if (shared)
                    {
                        if (!path.StartsWith(m_Shared + "/", StringComparison.Ordinal)) return false;
                        if (sharedOwners != null && (!sharedOwners.TryGetValue(guid, out var owner) || owner != path)) return false;
                    }
                    else if (path.StartsWith(m_Shared + "/", StringComparison.Ordinal) || Panel(imagePath) != Panel(path)) return false;
                    if (assigned.TryGetValue(guid, out var previous)) return previous == path;
                    assigned[guid] = path;
                    return true;
                });
                if (updated == text) continue;
                SharedSpriteIndex.AtomicWrite(path, updated);
                changed.Add(path);
            }
            if (changed.Count == 0) return;
            if (changed.Any(p => p.StartsWith(m_Shared + "/", StringComparison.Ordinal))) m_AtlasDirty = true;
            AssetDatabase.StartAssetEditing();
            try { foreach (var path in changed) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate); }
            finally { AssetDatabase.StopAssetEditing(); }
            foreach (var path in changed) m_Index.RefreshDocument(path);
        }

        private void PruneUnusedShared()
        {
            // Destruction must not rely on an incremental index alone: an import
            // interruption or an external edit can leave document owners absent.
            m_Index.ScanDocuments();
            var candidates = m_Index.images.Values.Where(i => Priority(i.path) == 1
                && !m_Index.Owners(i.guid).Any(p => !m_ManagedAtlases.Contains(p) || !p.StartsWith(m_Shared + "/", StringComparison.Ordinal))).ToArray();
            if (candidates.Length == 0) return;
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in Resources.FindObjectsOfTypeAll<Component>())
            {
                if (component == null || EditorUtility.IsPersistent(component) || !component.gameObject.scene.IsValid()) continue;
                var property = new SerializedObject(component).GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null)
                        live.Add(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(property.objectReferenceValue)));
            }
            // Remove atlas memberships first, before deleting an otherwise unused source.
            var unused = candidates.Where(i => !live.Contains(i.guid)).ToArray();
            RepairAtlasOwnership(new HashSet<string>(unused.Select(i => i.guid), StringComparer.Ordinal));
            foreach (var image in unused)
            {
                if (m_Index.Owners(image.guid).Any()) continue;
                if (AssetDatabase.DeleteAsset(image.path) && !File.Exists(image.path) && !File.Exists(image.path + ".meta"))
                { m_Index.RemoveImageAt(image.path); m_AtlasDirty = true; }
            }
        }
    }

    // Edits only the packables sequence, leaving master-atlas and other PPtrs intact.
    // Empty YAML fields are scalars; Unity requires an explicit empty sequence [].
    internal static class SharedSpriteAtlasLists
    {
        private static readonly Regex m_List = new Regex(@"(?m)^(?<indent>[ \t]*)(?<key>packables):[ \t]*(?<inline>[^\r\n]*)(?<newline>\r?\n|$)(?<items>(?:\k<indent>-[ \t]+[^\r\n]*(?:\r?\n|$))*)", RegexOptions.Compiled);
        private static readonly Regex m_Item = new Regex(@"[^\r\n]+(?:\r?\n|$)", RegexOptions.Compiled);
        private static readonly Regex m_Pointer = new Regex(@"\{\s*fileID:\s*(-?\d+),\s*guid:\s*([a-fA-F0-9]{32}),\s*type:\s*\d+\s*\}", RegexOptions.Compiled);

        internal static string Filter(string text, Func<string, bool> keep)
        {
            if (!m_List.IsMatch(text)) throw new InvalidDataException("SpriteAtlas packables sequence is missing.");
            return m_List.Replace(text, match =>
            {
                var inline = match.Groups["inline"].Value.Trim();
                if (inline.Length > 0 && inline != "[]" && inline != "null" && inline != "~")
                    throw new InvalidDataException("Unsupported SpriteAtlas packables value: " + inline);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var items = new StringBuilder();
                foreach (Match item in m_Item.Matches(match.Groups["items"].Value))
                {
                    var pointer = m_Pointer.Match(item.Value);
                    if (pointer.Success)
                    {
                        var guid = pointer.Groups[2].Value.ToLowerInvariant();
                        if (!keep(guid) || !seen.Add(guid + ":" + pointer.Groups[1].Value)) continue;
                    }
                    items.Append(item.Value);
                }
                var newline = match.Groups["newline"].Value;
                if (newline.Length == 0) newline = "\n";
                return match.Groups["indent"].Value + match.Groups["key"].Value + ":"
                    + (items.Length == 0 ? " []" + newline : newline + items);
            });
        }
    }

    // Editor notifications are kept separately from config serialization/fingerprints.
    internal sealed class SharedSpriteChanges : AssetPostprocessor
    {
        private static readonly Dictionary<string, long> m_Changes = new Dictionary<string, long>(StringComparer.Ordinal);
        internal static long revision { get; private set; }
        internal static IEnumerable<string> Since(long version) => m_Changes.Where(p => p.Value > version).Select(p => p.Key).ToArray();
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var path in imported.Concat(deleted).Concat(moved).Concat(movedFrom))
            {
                if (path.Contains(".akyui-shared-index.json") || path.EndsWith(".akyui-tmp", StringComparison.Ordinal)) continue;
                m_Changes[path] = ++revision;
            }
        }
    }

    [Serializable]
    internal sealed class SharedSpriteIndex
    {
        internal sealed class Replacement { internal string guid; internal readonly Dictionary<long, long> ids = new Dictionary<long, long>(); }
        [Serializable] internal sealed class Image { public string guid, path, stamp, revisionStamp, encoded, key, pixelHash; public int width, height, sourceWidth, sourceHeight; public long order; }
        [Serializable] internal sealed class Binding { public string guid, key, source; }
        [Serializable] internal sealed class Source { public string fingerprint; public Binding binding; }
        [Serializable] internal sealed class Document { public string path, stamp; public string[] references; }
        [Serializable] internal sealed class Migration { public string source, target; }
        [Serializable] private sealed class Data
        {
            public int version = 1;
            public string atlasPolicy;
            public string[] atlasPaths = Array.Empty<string>();
            public List<Image> images = new List<Image>();
            public List<Source> sources = new List<Source>();
            public List<Document> documents = new List<Document>();
            public List<Migration> pending = new List<Migration>();
        }
        private bool m_Dirty;
        private string m_JournalText;
        internal string atlasPolicy { get; private set; }
        internal string[] atlasPaths { get; private set; } = Array.Empty<string>();
        internal void MarkDirty() => m_Dirty = true;
        internal void SetAtlasPolicy(string value, string[] paths)
        {
            if (atlasPolicy == value && atlasPaths.SequenceEqual(paths)) return;
            atlasPolicy = value; atlasPaths = paths; m_Dirty = true;
        }
        internal readonly Dictionary<string, Image> images = new Dictionary<string, Image>(StringComparer.Ordinal);
        internal readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        internal readonly Dictionary<string, Document> documents = new Dictionary<string, Document>(StringComparer.Ordinal);
        internal readonly HashSet<string> changedKeys = new HashSet<string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> keyUpgrades = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly List<Migration> pending = new List<Migration>();
        private readonly Dictionary<string, HashSet<string>> m_Owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> m_ByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> m_ByEncoded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_SourceSlots = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_ByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        private long m_NextOrder;
        private static readonly Regex m_Reference = new Regex(@"\{\s*fileID:\s*(-?\d+),\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*(\d+)\s*\}", RegexOptions.Compiled);
        private static readonly HashSet<string> m_Extensions = new HashSet<string>(new[] { ".prefab", ".unity", ".asset", ".meta", ".mat", ".controller", ".overridecontroller", ".anim", ".playable", ".spriteatlas", ".spriteatlasv2" }, StringComparer.OrdinalIgnoreCase);

        internal static SharedSpriteIndex Load(string path)
        {
            var index = new SharedSpriteIndex();
            if (!File.Exists(path))
            {
                if (File.Exists(path + ".journal")) index.pending.AddRange(JsonUtility.FromJson<Data>(File.ReadAllText(path + ".journal")).pending);
                return index;
            }
            // Malformed state is an explicit failure, never a reason to delete files.
            var data = JsonUtility.FromJson<Data>(File.ReadAllText(path));
            if (data == null || data.version != 1) throw new InvalidDataException("Unsupported Shared index " + path);
            foreach (var image in data.images) { index.SetImage(image); index.changedKeys.Add(image.key); }
            foreach (var source in data.sources)
            {
                index.bindings[source.fingerprint] = source.binding;
                if (!string.IsNullOrEmpty(source.binding.source)) index.m_SourceSlots[source.binding.source] = source.fingerprint;
            }
            foreach (var document in data.documents) index.SetDocument(document);
            index.pending.AddRange(File.Exists(path + ".journal") ? JsonUtility.FromJson<Data>(File.ReadAllText(path + ".journal")).pending : data.pending);
            index.atlasPolicy = data.atlasPolicy;
            index.atlasPaths = data.atlasPaths ?? Array.Empty<string>();
            index.m_Dirty = false;
            return index;
        }

        internal void ApplyKeyUpgrades()
        {
            if (keyUpgrades.Count == 0) return;
            foreach (var binding in bindings.Values)
                if (keyUpgrades.TryGetValue(binding.key, out var key)) binding.key = key;
            keyUpgrades.Clear();
            m_Dirty = true;
        }
        internal void SaveJournal(string path)
        {
            var text = JsonUtility.ToJson(new Data { pending = pending.ToList() });
            if (m_JournalText == text) return;
            if (!File.Exists(path + ".journal") || File.ReadAllText(path + ".journal") != text) AtomicWrite(path + ".journal", text);
            m_JournalText = text;
        }
        internal void Bind(string source, string fingerprint, Image image)
        {
            m_SourceSlots[source] = fingerprint;
            if (bindings.TryGetValue(fingerprint, out var old) && old.guid == image.guid && old.key == image.key) return;
            bindings[fingerprint] = new Binding { source = source, guid = image.guid, key = image.key };
            m_Dirty = true;
        }
        internal IEnumerable<Image> Group(string key) => m_ByKey.TryGetValue(key, out var ids) ? ids.Select(id => images[id]) : Enumerable.Empty<Image>();
        internal Image FindEncoded(string key) => m_ByEncoded.TryGetValue(key, out var ids) ? ids.Select(id => images[id]).FirstOrDefault() : null;
        internal Image FindPath(string path) => m_ByPath.TryGetValue(path, out var guid) && images.TryGetValue(guid, out var image) ? image : null;
        internal void SetImage(Image image)
        {
            m_Dirty = true;
            if (m_ByPath.TryGetValue(image.path, out var previousGuid) && previousGuid != image.guid) RemoveImageAt(image.path);
            if (images.TryGetValue(image.guid, out var old))
            { RemoveGroup(m_ByKey, old.key, old.guid); RemoveGroup(m_ByEncoded, old.encoded, old.guid); m_ByPath.Remove(old.path); image.order = old.order; }
            if (image.order == 0) image.order = ++m_NextOrder;
            m_NextOrder = Math.Max(m_NextOrder, image.order);
            images[image.guid] = image;
            m_ByPath[image.path] = image.guid;
            if (!m_ByKey.TryGetValue(image.key, out var keys)) m_ByKey[image.key] = keys = new HashSet<string>(StringComparer.Ordinal);
            if (!m_ByEncoded.TryGetValue(image.encoded, out var encoded)) m_ByEncoded[image.encoded] = encoded = new HashSet<string>(StringComparer.Ordinal);
            keys.Add(image.guid); encoded.Add(image.guid);
        }
        internal void UpdateImagePath(Image image, string previousPath) { m_ByPath.Remove(previousPath); m_ByPath[image.path] = image.guid; m_Dirty = true; }

        internal void Save(string path)
        {
            if (!m_Dirty) return;
            foreach (var source in bindings.Where(p => (!images.TryGetValue(p.Value.guid, out var image) || image.key != p.Value.key)).Select(p => p.Key).ToArray()) bindings.Remove(source);
            foreach (var slot in m_SourceSlots.Where(p => !bindings.ContainsKey(p.Value)).Select(p => p.Key).ToArray()) m_SourceSlots.Remove(slot);
            var data = new Data { atlasPolicy = atlasPolicy, atlasPaths = atlasPaths, images = images.Values.OrderBy(i => i.guid, StringComparer.Ordinal).ToList(),
                sources = bindings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new Source { fingerprint = p.Key, binding = p.Value }).ToList(),
                documents = documents.Values.OrderBy(d => d.path, StringComparer.Ordinal).ToList(), pending = pending.ToList() };
            var text = JsonUtility.ToJson(data);
            if (!File.Exists(path) || File.ReadAllText(path) != text) AtomicWrite(path, text);
            SaveJournal(path);
            m_Dirty = false;
        }

        internal void ScanDocuments()
        {
            foreach (var path in Directory.EnumerateFiles("Assets", "*", SearchOption.AllDirectories))
            {
                if (!m_Extensions.Contains(Path.GetExtension(path))) continue;
                Importer.CurrentProgress?.Update("建立共享引用索引 " + Path.GetFileName(path), .90f);
                RefreshDocument(path.Replace('\\', '/'));
            }
            foreach (var path in documents.Keys.Where(p => !File.Exists(p)).ToArray()) RemoveDocument(path);
        }

        internal void RemoveMissingImages()
        {
            foreach (var path in images.Values.Where(p => !File.Exists(p.path)).Select(p => p.path).ToArray()) RemoveImageAt(path);
        }
        internal void RemoveImageAt(string path)
        {
            if (!m_ByPath.TryGetValue(path, out var guid) || !images.TryGetValue(guid, out var image)) return;
            m_Dirty = true;
            images.Remove(guid); m_ByPath.Remove(path); RemoveGroup(m_ByKey, image.key, guid); RemoveGroup(m_ByEncoded, image.encoded, guid); changedKeys.Add(image.key);
        }
        private static void RemoveGroup(Dictionary<string, HashSet<string>> groups, string key, string guid)
        { if (groups.TryGetValue(key, out var values)) { values.Remove(guid); if (values.Count == 0) groups.Remove(key); } }
        internal IEnumerable<string> Owners(string guid) => m_Owners.TryGetValue(guid, out var owners) ? owners : Enumerable.Empty<string>();
        internal static string Stamp(string path) { var file = new FileInfo(path); return file.Length + ":" + file.LastWriteTimeUtc.Ticks; }
        internal static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes)); }

        internal void RefreshDocument(string path)
        {
            if (!m_Extensions.Contains(Path.GetExtension(path))) return;
            if (!File.Exists(path)) { RemoveDocument(path); return; }
            var stamp = Stamp(path);
            if (documents.TryGetValue(path, out var existing) && existing.stamp == stamp) return;
            var refs = ReadReferences(path);
            SetDocument(new Document { path = path, stamp = stamp, references = refs });
        }
        private static string[] ReadReferences(string path)
        {
            // Font assets and serialized textures may contain enormous inline blobs.
            // Scan their PPtrs in bounded chunks instead of retaining the whole file.
            using (var reader = new StreamReader(path))
            {
                var buffer = new char[32768];
                var read = reader.Read(buffer, 0, buffer.Length);
                var first = new string(buffer, 0, read);
                if (!first.StartsWith("%YAML", StringComparison.Ordinal) && !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    return AssetDatabase.GetDependencies(path, false).Where(p => p != path).Select(AssetDatabase.AssetPathToGUID).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToArray();
                var refs = new HashSet<string>(StringComparer.Ordinal);
                var chunk = first;
                while (true)
                {
                    foreach (Match match in m_Reference.Matches(chunk)) refs.Add(match.Groups[2].Value.ToLowerInvariant());
                    read = reader.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    chunk = chunk.Substring(Math.Max(0, chunk.Length - 256)) + new string(buffer, 0, read);
                }
                return refs.ToArray();
            }
        }
        private void RemoveDocument(string path)
        {
            if (!documents.TryGetValue(path, out var document)) return;
            foreach (var guid in document.references) RemoveGroup(m_Owners, guid, path);
            documents.Remove(path);
            m_Dirty = true;
        }
        private void SetDocument(Document document)
        {
            RemoveDocument(document.path);
            m_Dirty = true;
            documents[document.path] = document;
            foreach (var guid in document.references)
            {
                if (!m_Owners.TryGetValue(guid, out var owners)) m_Owners[guid] = owners = new HashSet<string>(StringComparer.Ordinal);
                owners.Add(document.path);
            }
        }

        internal void ReplaceReferences(string path, IDictionary<string, Replacement> replacements, IDictionary<Object, Object> objects)
        {
            var text = File.ReadAllText(path);
            if (text.StartsWith("%YAML", StringComparison.Ordinal) || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                var updated = m_Reference.Replace(text, match =>
                {
                    if (!replacements.TryGetValue(match.Groups[2].Value.ToLowerInvariant(), out var replacement)) return match.Value;
                    var oldId = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (!replacement.ids.TryGetValue(oldId, out var newId)) throw new InvalidDataException("未知子资源引用，保留原图: " + path + ":" + oldId);
                    return "{fileID: " + newId.ToString(CultureInfo.InvariantCulture) + ", guid: " + replacement.guid + ", type: " + match.Groups[3].Value + "}";
                });
                if (text != updated)
                {
                    AtomicWrite(path, updated);
                    AssetDatabase.ImportAsset(path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 5) : path, ImportAssetOptions.ForceSynchronousImport);
                }
            }
            else
            {
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path)) if (asset != null) ReplaceObjectReferences(asset, objects, false);
                AssetDatabase.SaveAssets();
            }
            // Force dependency refresh even for filesystems with coarse timestamps.
            RemoveDocument(path);
            RefreshDocument(path);
        }

        internal static bool ReplaceObjectReferences(Object value, IDictionary<Object, Object> map, bool undo)
        {
            var serialized = new SerializedObject(value);
            var property = serialized.GetIterator();
            var changed = false;
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference || property.objectReferenceValue == null
                    || !map.TryGetValue(property.objectReferenceValue, out var target)) continue;
                if (!changed && undo) Undo.RecordObject(value, "迁移共享图片引用");
                property.objectReferenceValue = target; changed = true;
            }
            if (changed) { serialized.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(value); }
            return changed;
        }

        internal static void AtomicWrite(string path, string text)
        {
            var temporary = path + ".akyui-tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                        break;
                    }
                    catch (IOException) when (attempt < 7 && File.Exists(temporary))
                    {
                        // Windows import workers/virus scanners can briefly hold
                        // readers without FILE_SHARE_DELETE. Keep the original and
                        // retry the atomic operation; never fall back to truncation.
                        System.Threading.Thread.Sleep(Math.Min(20 << attempt, 200));
                    }
                }
            }
            catch (IOException error) { throw new IOException("Cannot atomically update " + path, error); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
