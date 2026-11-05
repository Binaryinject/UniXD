using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BIEngine.UniXD.Editor.Extensions;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Generator;
using BIEngine.UniXD.Generator.InternalTrigger;
using BIEngine.UniXD.Loader;
using BIEngine.UniXD.Loader.Internal;
using UnityEngine;
using UnityEditor;
using UnityEditor.Presets;
using Utf8Json;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.Editor
{
    public static class Importer
    {
        public static IImportSettings Settings { get; private set; }
        internal static ImportFingerprintSession Fingerprints { get; private set; }
        internal static IImportProgress CurrentProgress { get; private set; }

        public static void Import(IImportSettings settings, string[] filePaths, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layoutLoaders = filePaths
                .Select(x => (ILoader) new LayoutLoader(x))
                .ToArray();

            try { Import(settings, layoutLoaders, cancellationToken); }
            finally { foreach (var layoutLoader in layoutLoaders) layoutLoader.Dispose(); }
        }

        public static void Import(IImportSettings settings, ILoader[] loaders, CancellationToken cancellationToken = default)
        {
            var logger = new ImportLogger("UniXD", settings.LogType);
            using (var progressBar = new ImportProgressBar("导入 Unity UI", cancellationToken))
            {
                progressBar.SetTotal(loaders.Length);
                var succeeded = false;
                try
                {
                    foreach (var loader in loaders)
                    {
                        using (logger.SetCategory(loader.LayoutInfo.Name))
                        using (var progress = progressBar.TaskStart($"Importing {loader.LayoutInfo.Name}"))
                        {
                            Import(settings, loader, logger, progress);
                            progress.Update("画板导入完成", 1f);
                        }
                    }

                    progressBar.Update("保存导入结果", 1f);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    succeeded = true;
                }
                finally
                {
                    try
                    {
                        foreach (var trigger in settings.Triggers.OfType<ImportTrigger>())
                            trigger.OnImportBatchCompleted(settings, succeeded);
                    }
                    finally { ImportContentCache.Flush(); }
                }
            }
        }

        public static void Save(ILoader loader, string filePath)
        {
            var bytes = LayoutCompressor.Compress(loader);
            File.WriteAllBytes(filePath, bytes);
        }

        private static void CheckVersion(ILoader loader)
        {
            var loaderVersion = loader.LayoutInfo.Meta.FormatVersion;
            if (loaderVersion == Const.FormatVersion) return;
            throw new Exception($"Cannot load version {loaderVersion} file. (Importer version is {Const.FormatVersion})");
        }

        private static void Import(IImportSettings settings, ILoader layoutLoader, ImportLogger logger, IImportProgress progress)
        {
            var stopWatch = Stopwatch.StartNew();
            CheckVersion(layoutLoader);

            Settings = settings;
            using (Disposable.Create(() => { Settings = null; Fingerprints = null; CurrentProgress = null; }))
            {
                CurrentProgress = progress;
                var pathGetter = new PathGetter(settings, layoutLoader.LayoutInfo.Name);
                var prevMetaGameObject = AssetDatabase.LoadAssetAtPath<GameObject>(pathGetter.MetaSavePath);
                var prevMeta = prevMetaGameObject != null ? prevMetaGameObject.GetComponent<PrefabMeta>() : null;
                var prevAssets = prevMeta != null ? prevMeta.assets : new Object[] { };

                Fingerprints = new ImportFingerprintSession(settings, layoutLoader, pathGetter);
                var (assets, importAssetsLog) = ImportAssets(settings, layoutLoader, pathGetter, logger, progress);
                var layoutFingerprint = Fingerprints.GetLayoutFingerprint(assets);
                if (!settings.ReimportLayout && importAssetsLog.Import == 0 && prevMeta != null &&
                    prevMeta.FindUserData(ImportFingerprintSession.LayoutKey)?.value == layoutFingerprint &&
                    AssetDatabase.LoadAssetAtPath<GameObject>(pathGetter.PrefabSavePath) != null)
                {
                    logger.Log("Skip Layout", ("Name", layoutLoader.LayoutInfo.Name), ("ImportAssets", 0), ("SkipAssets", importAssetsLog.Skip),
                        ("TotalTime", $"{stopWatch.Elapsed.TotalSeconds:0.00}s"));
                    foreach (var trigger in settings.Triggers.OfType<ImportTrigger>()) trigger.OnImportCompleted(layoutLoader, pathGetter);
                    return;
                }
                // Save one complete prefab/meta pair without interruption between the two writes.
                progress.Update("生成并保存预制体（当前步骤完成后可取消）", .95f);
                var (gameObject, hash, eidMap, importLayoutLog) = ImportLayout(settings, layoutLoader, pathGetter, logger);

                EditorUtil.CreateDirectory(Path.GetDirectoryName(pathGetter.PrefabSavePath));
                EditorUtil.CreateDirectory(Path.GetDirectoryName(pathGetter.MetaSavePath));
                var preparedPrefab = PreparePrefabForSave(pathGetter, prevMeta, gameObject, eidMap);
                Dictionary<uint, string> eidPaths;
                try
                {
                    eidPaths = BuildEidPaths(preparedPrefab.EidMap, preparedPrefab.Root);
                    PrefabUtility.SaveAsPrefabAsset(preparedPrefab.Root, pathGetter.PrefabSavePath);
                }
                finally { CleanupPreparedPrefab(preparedPrefab); }
                SaveMetaPrefab(pathGetter, layoutLoader, hash, assets, eidPaths, layoutFingerprint);

                foreach (var trigger in settings.Triggers) trigger.OnPostprocessFile(layoutLoader, pathGetter);
                DeleteUnusedAssets(prevAssets, assets, logger, pathGetter.AssetOutputDirectoryPath);
                foreach (var trigger in settings.Triggers.OfType<ImportTrigger>()) trigger.OnImportCompleted(layoutLoader, pathGetter);

                stopWatch.Stop();
                logger.Log($"Import Finish",
                    ("TotalTime", $"{stopWatch.Elapsed.TotalSeconds:0.00}s"),
                    ("AssetsImportTime", $"{importAssetsLog.Time:0.00}s"),
                    ("LayoutImportTime", $"{importLayoutLog.Time:0.00}s"),
                    ("ImportAssets", $"{importAssetsLog.Import}"),
                    ("SkipAssets", $"{importAssetsLog.Skip}")
                );
            }
        }

        private static string[] DeleteUnusedAssets(Object[] prevAssets, Object[] newAssets, ImportLogger logger, string ownedDirectory)
        {
            var deletedUnusedAssets = new List<string>();
            var newAssetPaths = new HashSet<string>(newAssets
                .Where(x => x != null)
                .Select(AssetDatabase.GetAssetPath)
                .Where(x => !string.IsNullOrWhiteSpace(x)));

            foreach (var prevAsset in prevAssets)
            {
                if (prevAsset == null) continue;

                var prevAssetPath = AssetDatabase.GetAssetPath(prevAsset);
                if (string.IsNullOrWhiteSpace(prevAssetPath)) continue;
                if (newAssetPaths.Contains(prevAssetPath)) continue;
                // Metadata may include dependencies redirected outside this panel.
                if (!prevAssetPath.StartsWith(ownedDirectory.TrimEnd('/', '\\') + "/", StringComparison.OrdinalIgnoreCase)) continue;

                deletedUnusedAssets.Add(Path.GetFileName(prevAssetPath));
                AssetDatabase.DeleteAsset(prevAssetPath);
            }

            return deletedUnusedAssets.ToArray();
        }

        private class ImportAssetsLog
        {
            public double Time { get; set; }
            public int Import { get; set; }
            public int Skip { get; set; }
        }

        private sealed class PendingAssetImport
        {
            public IAsset Asset;
            public string Path;
            public string FullPath;
            public byte[] Bytes;
            public Dictionary<string, object> UserData;
        }

        private static (Object[], ImportAssetsLog) ImportAssets(IImportSettings settings, ILoader layoutLoader, PathGetter pathGetter, ImportLogger logger, IImportProgress progress)
        {
            var stopWatch = Stopwatch.StartNew();
            var assetPaths = new List<string>();
            var pendingImports = new List<PendingAssetImport>();
            var unityAssetsParentPath = Path.GetDirectoryName(Application.dataPath) ?? "";

            var assetOutputDirectoryFullPath = Path.Combine(unityAssetsParentPath, pathGetter.AssetOutputDirectoryPath);
            if (!Directory.Exists(assetOutputDirectoryFullPath)) Directory.CreateDirectory(assetOutputDirectoryFullPath);

            var importAssetNames = new List<string>();
            var skipAssetNames = new List<string>();

            var allAssets = layoutLoader.AssetsInfo.Assets.ToList();
            progress.Update("检查图片与处理配置", 0f);
            foreach (var trigger in settings.Triggers) trigger.OnPreprocessAllAssets(layoutLoader, ref allAssets);

            progress.SetTotal(allAssets.Count);
            foreach (var tmp in allAssets)
            {
                var asset = tmp;
                using (progress.TaskStart(asset.FileName))
                {
                    var savePath = NormalizeAssetPath(Path.Combine(pathGetter.AssetOutputDirectoryPath, asset.FileName));
                    var saveFullPath = Path.Combine(unityAssetsParentPath, savePath);
                    var saveDirectory = Path.GetDirectoryName(saveFullPath);
                    if (!string.IsNullOrEmpty(saveDirectory)) Directory.CreateDirectory(saveDirectory);

                    var fingerprint = Fingerprints.GetFingerprint(asset);
                    if (!settings.ReimportAsset && Fingerprints.TryGetPrevious(asset, out _))
                    {
                        skipAssetNames.Add(asset.FileName);
                        assetPaths.Add(savePath);
                        continue;
                    }

                    var bytes = Fingerprints.LoadAsset(asset.FileName);
                    var userData = new Dictionary<string, object>();
                    userData["hash"] = asset.Hash;

                    if (asset is SpriteAsset)
                    {
                        var texture = new Texture2D(2, 2);
                        try
                        {
                            texture.LoadImage(bytes);
                            userData["source_width"] = texture.width;
                            userData["source_height"] = texture.height;
                        }
                        finally
                        {
                            Object.DestroyImmediate(texture);
                        }
                    }

                    // Triggers may set borders or replace an asset; keep loader descriptors immutable.
                    if (asset is SpriteAsset sourceSprite)
                        asset = new SpriteAsset(sourceSprite.FileName, sourceSprite.Hash, sourceSprite.Size, sourceSprite.UserData,
                            sourceSprite.Border == null ? null : new Border(sourceSprite.Border.Top, sourceSprite.Border.Right, sourceSprite.Border.Bottom, sourceSprite.Border.Left));
                    foreach (var trigger in settings.Triggers) trigger.OnPreprocessAsset(layoutLoader, ref bytes, ref asset, ref userData);
                    userData[ImportFingerprintSession.AssetKey] = fingerprint;
                    userData[ImportFingerprintSession.OutputKey] = ImportFingerprintSession.Hash(bytes);
                    userData[ImportFingerprintSession.ProcessedKey] = ImportFingerprintSession.ProcessedHash(bytes, (asset as SpriteAsset)?.Border).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    pendingImports.Add(new PendingAssetImport
                    {
                        Asset = asset, Path = savePath, FullPath = saveFullPath, Bytes = bytes, UserData = userData
                    });
                    assetPaths.Add(savePath);
                    importAssetNames.Add(asset.FileName);
                }
            }

            // SVG generation above uses synchronous imports, so keep it outside asset editing.
            // Register new files together before requesting their TextureImporters.
            if (pendingImports.Count > 0)
            {
                // Finish both registration/settings phases before honoring cancellation, so
                // a cancel click cannot leave new textures half configured.
                progress.Update("批量导入图片（当前步骤完成后可取消）", .85f);
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var pending in pendingImports)
                    {
                        if (!(pending.Asset is SpriteAsset) && !(pending.Asset is VectorSpriteAsset)) continue;
                        var isNew = AssetImporter.GetAtPath(pending.Path) == null;
                        File.WriteAllBytes(pending.FullPath, pending.Bytes);
                        pending.Bytes = null;
                        if (isNew) AssetDatabase.ImportAsset(pending.Path);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                // Apply presets, borders and trigger metadata, then import as one batch.
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var pending in pendingImports)
                        ImportAsset(pending.Asset, pending.Path, pending.UserData, settings, logger);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }

            progress.Update("图片导入完成", .90f);
            // Imported objects are available only after StopAssetEditing.
            var importAssets = assetPaths.Select(AssetDatabase.LoadAssetAtPath<Object>).ToArray();
            foreach (var trigger in settings.Triggers) trigger.OnPostprocessAllAssets(layoutLoader, importAssets);

            // Atlas hooks receive only this panel's physical imports. Metadata also
            // records redirected dependencies, so plugins can track their owners.
            var resolver = new EditorAssetLoader(pathGetter, logger, settings.Triggers);
            importAssets = importAssets.Concat(layoutLoader.AssetsInfo.Assets
                .Select(a => AssetDatabase.LoadAssetAtPath<Object>(resolver.ResolveAssetPath(a.FileName))))
                .Where(a => a != null).Distinct().ToArray();

            return (
                importAssets,
                new ImportAssetsLog { Time = stopWatch.Elapsed.TotalSeconds, Import = importAssetNames.Count,
                    Skip = skipAssetNames.Count + layoutLoader.AssetsInfo.Assets.Length - allAssets.Count }
            );
        }

        private static void ImportAsset(IAsset asset, string savePath, Dictionary<string, object> userData, IImportSettings importSettings, ImportLogger logger)
        {
            var preset = importSettings.TexturePreset;
            var triggers = importSettings.Triggers;

            if (asset is VectorSpriteAsset vectorAsset)
            {
                var svgImporter = AssetImporter.GetAtPath(savePath) as Unity.VectorGraphics.Editor.SVGImporter;
                if (svgImporter == null) throw new InvalidOperationException("SVG 导入器不可用：" + savePath);
                svgImporter.SvgType = Unity.VectorGraphics.Editor.SVGType.UISVGImage;
                svgImporter.ViewportOptions = vectorAsset.PreserveViewport
                    ? Unity.VectorGraphics.ViewportOptions.PreserveViewport : Unity.VectorGraphics.ViewportOptions.DontPreserve;
                svgImporter.PreserveSVGImageAspect = false;
                svgImporter.GeneratePhysicsShape = false;
                foreach (var trigger in triggers) trigger.OnUnityPreprocessAsset(svgImporter, asset, ref userData);
                svgImporter.userData = JsonSerializer.ToJsonString(userData);
                EditorUtility.SetDirty(svgImporter);
                AssetDatabase.WriteImportSettingsIfDirty(savePath);
                AssetDatabase.ImportAsset(savePath);
                return;
            }

            if (asset is SpriteAsset)
            {
                var assetImporter = AssetImporter.GetAtPath(savePath);

                if (assetImporter == null) {
                    throw new Exception($"assetImporter() == null; assetType={asset.GetType().FullName}, assetFileName={asset.FileName}, savePath={savePath}");
                }
                var textureImporter = (TextureImporter) assetImporter;

                if (preset != null)
                {
                    preset.ApplyTo(textureImporter);
                }

                textureImporter.textureType = TextureImporterType.Sprite;
                // Each exported file represents one sprite. Unity 6 may otherwise
                // retain an automatic multiple-sprite rect after the sliced size changes.
                textureImporter.spriteImportMode = SpriteImportMode.Single;

                if (asset is SpriteAsset spriteAsset)
                {
                    textureImporter.spriteBorder = spriteAsset.Border?.ToVector4() ?? Vector4.zero;
                    textureImporter.maxTextureSize = Mathf.RoundToInt(Mathf.Max(spriteAsset.Size.x, spriteAsset.Size.y) * Settings.SpriteSaveScale);
                }

                foreach (var trigger in triggers) trigger.OnUnityPreprocessAsset(assetImporter, asset, ref userData);
                assetImporter.userData = JsonSerializer.ToJsonString(userData);
                AssetDatabase.WriteImportSettingsIfDirty(savePath);

                AssetDatabase.ImportAsset(savePath);
                return;
            }

            logger.Error($"Unknown asset type {asset}");
        }

        internal static string NormalizeAssetPath(string path)
        {
            return string.Join("/", path.Replace('\\', '/').Split('/')
                .Select(segment => segment.Trim())
                .Where(segment => !string.IsNullOrEmpty(segment)));
        }

        private class ImportLayoutLog
        {
            public double Time { get; set; }
        }

        private static (GameObject, uint Hash, IDictionary<uint, GameObject> EidMap, ImportLayoutLog) ImportLayout(IImportSettings settings, ILoader layoutLoader, PathGetter pathGetter, ImportLogger logger)
        {
            var stopWatch = Stopwatch.StartNew();
            var layoutInfo = layoutLoader.LayoutInfo;
            var triggers = settings.Triggers.Select(x => (IGenerateTrigger) x).ToArray();
            var (gameObject, hash, eidMap) = PrefabGenerator.GenerateGameObject(new EditorAssetLoader(pathGetter, logger, settings.Triggers), layoutInfo, triggers);
            foreach (var trigger in settings.Triggers) trigger.OnPostprocessPrefab(layoutLoader, ref gameObject);
            if (settings is IPrefabAppearanceSettings appearance) appearance.ApplyAppearance(gameObject, pathGetter);
            return (gameObject, hash, eidMap, new ImportLayoutLog { Time = stopWatch.Elapsed.TotalSeconds });
        }

        private sealed class PreparedPrefab
        {
            public GameObject Root { get; set; }
            public IDictionary<uint, GameObject> EidMap { get; set; }
            public GameObject GeneratedRoot { get; set; }
            public bool RootLoadedFromPrefabContents { get; set; }
        }

        private sealed class PrefabMergeContext
        {
            private readonly Dictionary<GameObject, uint> _sourceGameObjectToEid;
            private readonly HashSet<GameObject> m_PreviousEidGameObjects;
            private readonly Dictionary<UnityEngine.Object, UnityEngine.Object> _sourceToDestinationObjectMap = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
            private readonly HashSet<GameObject> _usedDestinationGameObjects = new HashSet<GameObject>();
            private readonly List<Component> _componentsToRemove = new List<Component>();

            public IDictionary<uint, GameObject> PreviousEidMap { get; }
            public Dictionary<uint, GameObject> FinalEidMap { get; } = new Dictionary<uint, GameObject>();
            public IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> SourceToDestinationObjectMap => _sourceToDestinationObjectMap;
            public IReadOnlyList<Component> ComponentsToRemove => _componentsToRemove;

            public PrefabMergeContext(IDictionary<uint, GameObject> sourceEidMap, IDictionary<uint, GameObject> previousEidMap)
            {
                _sourceGameObjectToEid = sourceEidMap.ToDictionary(x => x.Value, x => x.Key);
                PreviousEidMap = previousEidMap;
                m_PreviousEidGameObjects = new HashSet<GameObject>(previousEidMap.Values);
            }

            public bool TryGetEid(GameObject sourceGameObject, out uint eid) => _sourceGameObjectToEid.TryGetValue(sourceGameObject, out eid);

            public bool TryGetPreviousGameObject(uint eid, out GameObject gameObject) => PreviousEidMap.TryGetValue(eid, out gameObject);

            public bool IsUsed(GameObject gameObject) => _usedDestinationGameObjects.Contains(gameObject);

            public bool HasPreviousEid(GameObject gameObject) => m_PreviousEidGameObjects.Contains(gameObject);

            public void Register(GameObject sourceGameObject, GameObject destinationGameObject)
            {
                // Component generators can add Content, Handle and Placeholder objects without a layout EID.
                if (TryGetEid(sourceGameObject, out var eid)) FinalEidMap[eid] = destinationGameObject;
                _usedDestinationGameObjects.Add(destinationGameObject);
                _sourceToDestinationObjectMap[sourceGameObject] = destinationGameObject;
                _sourceToDestinationObjectMap[sourceGameObject.transform] = destinationGameObject.transform;
            }

            public void Register(Component sourceComponent, Component destinationComponent)
            {
                _sourceToDestinationObjectMap[sourceComponent] = destinationComponent;
            }

            public bool TryGetDestinationObject(UnityEngine.Object sourceObject, out UnityEngine.Object destinationObject) => _sourceToDestinationObjectMap.TryGetValue(sourceObject, out destinationObject);

            public void AddComponentToRemove(Component component)
            {
                _componentsToRemove.Add(component);
            }
        }

        private static PreparedPrefab PreparePrefabForSave(PathGetter pathGetter, PrefabMeta prevMeta, GameObject generatedRoot, IDictionary<uint, GameObject> generatedEidMap)
        {
            GameObject loadedRoot = null;
            try
            {
                if (!CanReusePreviousPrefab(pathGetter, prevMeta))
                {
                    return CreatePreparedPrefabFromGenerated(generatedRoot, generatedEidMap);
                }

                loadedRoot = PrefabUtility.LoadPrefabContents(pathGetter.PrefabSavePath);
                var previousEidMap = BuildPreviousEidMap(prevMeta, loadedRoot);
                if (!previousEidMap.ContainsKey(0))
                {
                    PrefabUtility.UnloadPrefabContents(loadedRoot);
                    loadedRoot = null;
                    return CreatePreparedPrefabFromGenerated(generatedRoot, generatedEidMap);
                }

                var mergeContext = new PrefabMergeContext(generatedEidMap, previousEidMap);
                // 按 eid 复用旧节点，尽量保住 Unity 为旧对象分配的 fileID。
                BuildMergedPrefabTree(generatedRoot, loadedRoot, null, 0, mergeContext);
                RemoveUnusedChildrenRecursive(loadedRoot, mergeContext);
                RemoveExtraComponents(mergeContext.ComponentsToRemove);
                CopyMergedPrefabTree(generatedRoot, mergeContext);

                return new PreparedPrefab
                {
                    Root = loadedRoot,
                    EidMap = mergeContext.FinalEidMap,
                    GeneratedRoot = generatedRoot,
                    RootLoadedFromPrefabContents = true,
                };
            }
            catch
            {
                try
                {
                    if (loadedRoot != null) PrefabUtility.UnloadPrefabContents(loadedRoot);
                }
                finally
                {
                    if (generatedRoot != null) Object.DestroyImmediate(generatedRoot);
                }

                throw;
            }
        }

        private static bool CanReusePreviousPrefab(PathGetter pathGetter, PrefabMeta prevMeta)
        {
            return File.Exists(pathGetter.PrefabSavePath)
                && prevMeta != null
                && prevMeta.root != null
                && prevMeta.eidToObjects != null
                && prevMeta.eidToObjects.Length > 0;
        }

        private static PreparedPrefab CreatePreparedPrefabFromGenerated(GameObject generatedRoot, IDictionary<uint, GameObject> generatedEidMap)
        {
            return new PreparedPrefab
            {
                Root = generatedRoot,
                EidMap = new Dictionary<uint, GameObject>(generatedEidMap),
                GeneratedRoot = generatedRoot,
                RootLoadedFromPrefabContents = false,
            };
        }

        private static Dictionary<uint, GameObject> BuildPreviousEidMap(PrefabMeta prevMeta, GameObject loadedRoot)
        {
            var previousEidMap = new Dictionary<uint, GameObject>();
            if (loadedRoot == null) return previousEidMap;

            foreach (var eidToObject in prevMeta.eidToObjects)
            {
                if (eidToObject?.gameObject == null) continue;

                var path = AnimationUtility.CalculateTransformPath(eidToObject.gameObject.transform, prevMeta.root.transform);
                var gameObject = FindGameObjectByPath(loadedRoot, path);
                if (gameObject == null) continue;
                previousEidMap[eidToObject.eid] = gameObject;
            }

            return previousEidMap;
        }

        private static void BuildMergedPrefabTree(GameObject sourceGameObject, GameObject destinationGameObject, Transform destinationParent, int siblingIndex, PrefabMergeContext context)
        {
            if (destinationParent != null && destinationGameObject.transform.parent != destinationParent)
            {
                destinationGameObject.transform.SetParent(destinationParent, false);
            }

            if (destinationGameObject.transform.parent == destinationParent)
            {
                destinationGameObject.transform.SetSiblingIndex(siblingIndex);
            }

            context.Register(sourceGameObject, destinationGameObject);
            MapComponents(sourceGameObject, destinationGameObject, context);

            for (var i = 0; i < sourceGameObject.transform.childCount; i++)
            {
                var sourceChild = sourceGameObject.transform.GetChild(i).gameObject;
                var destinationChild = ResolveDestinationChild(sourceChild, destinationGameObject.transform, context);
                BuildMergedPrefabTree(sourceChild, destinationChild, destinationGameObject.transform, i, context);
            }
        }

        private static void MapComponents(GameObject sourceGameObject, GameObject destinationGameObject, PrefabMergeContext context)
        {
            var sourceComponents = GetNonTransformComponents(sourceGameObject).ToArray();
            var destinationComponents = GetNonTransformComponents(destinationGameObject).ToList();
            var matchedDestinationComponents = new HashSet<Component>();

            // uGUI permits only one Graphic on a GameObject. Remove the previous
            // Image/SVGImage before adding its replacement during a mode switch.
            var sourceGraphic = sourceComponents.OfType<UnityEngine.UI.Graphic>().FirstOrDefault();
            if (sourceGraphic != null)
                foreach (var oldGraphic in destinationComponents.OfType<UnityEngine.UI.Graphic>()
                    .Where(x => x.GetType() != sourceGraphic.GetType()).ToArray())
                {
                    destinationComponents.Remove(oldGraphic);
                    Object.DestroyImmediate(oldGraphic);
                }

            foreach (var sourceComponent in sourceComponents)
            {
                var destinationComponent = destinationComponents.FirstOrDefault(x => !matchedDestinationComponents.Contains(x) && x.GetType() == sourceComponent.GetType());
                if (destinationComponent == null)
                {
                    destinationComponent = destinationGameObject.AddComponent(sourceComponent.GetType());
                    destinationComponents.Add(destinationComponent);
                }

                matchedDestinationComponents.Add(destinationComponent);
                context.Register(sourceComponent, destinationComponent);
            }

            foreach (var destinationComponent in destinationComponents.Where(x => !matchedDestinationComponents.Contains(x)))
            {
                context.AddComponentToRemove(destinationComponent);
            }
        }

        private static void RemoveUnusedChildrenRecursive(GameObject destinationGameObject, PrefabMergeContext context)
        {
            for (var i = destinationGameObject.transform.childCount - 1; i >= 0; i--)
            {
                var child = destinationGameObject.transform.GetChild(i).gameObject;
                if (context.IsUsed(child))
                {
                    RemoveUnusedChildrenRecursive(child, context);
                    continue;
                }

                Object.DestroyImmediate(child);
            }
        }

        private static GameObject CreateGameObjectLike(GameObject sourceGameObject)
        {
            return sourceGameObject.transform is RectTransform
                ? new GameObject(sourceGameObject.name, typeof(RectTransform))
                : new GameObject(sourceGameObject.name);
        }

        private static GameObject ResolveDestinationChild(GameObject sourceChild, Transform destinationParent, PrefabMergeContext context)
        {
            if (context.TryGetEid(sourceChild, out var childEid))
            {
                if (context.TryGetPreviousGameObject(childEid, out var previousAssetGameObject) && previousAssetGameObject != null && !context.IsUsed(previousAssetGameObject))
                {
                    return previousAssetGameObject;
                }
            }
            else
            {
                // Match generated helper nodes only within their matched parent. Repeated names
                // consume separate siblings in order; layout nodes remain reserved for EID matching.
                for (var i = 0; i < destinationParent.childCount; i++)
                {
                    var candidate = destinationParent.GetChild(i).gameObject;
                    if (!context.IsUsed(candidate) && !context.HasPreviousEid(candidate)
                        && candidate.name == sourceChild.name && candidate.transform.GetType() == sourceChild.transform.GetType())
                    {
                        return candidate;
                    }
                }
            }

            return CreateGameObjectLike(sourceChild);
        }

        private static void CopyMergedPrefabTree(GameObject sourceGameObject, PrefabMergeContext context)
        {
            if (!context.TryGetDestinationObject(sourceGameObject, out var destinationObject)) return;
            var destinationGameObject = (GameObject) destinationObject;
            CopyGameObjectState(sourceGameObject, destinationGameObject);
            CopyComponentState((Component) sourceGameObject.transform, (Component) destinationGameObject.transform, context.SourceToDestinationObjectMap);

            foreach (var sourceComponent in GetNonTransformComponents(sourceGameObject))
            {
                if (!context.TryGetDestinationObject(sourceComponent, out var destinationComponentObject)) continue;
                CopyComponentState(sourceComponent, (Component) destinationComponentObject, context.SourceToDestinationObjectMap);
            }

            for (var i = 0; i < sourceGameObject.transform.childCount; i++)
            {
                CopyMergedPrefabTree(sourceGameObject.transform.GetChild(i).gameObject, context);
            }
        }

        private static void CopyGameObjectState(GameObject sourceGameObject, GameObject destinationGameObject)
        {
            destinationGameObject.name = sourceGameObject.name;
            destinationGameObject.tag = sourceGameObject.tag;
            destinationGameObject.layer = sourceGameObject.layer;
            destinationGameObject.isStatic = sourceGameObject.isStatic;
            destinationGameObject.SetActive(sourceGameObject.activeSelf);
        }

        private static void CopyComponentState(Component sourceComponent, Component destinationComponent, IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> sourceToDestinationObjectMap)
        {
            EditorUtility.CopySerialized(sourceComponent, destinationComponent);
            var serializedObject = new SerializedObject(destinationComponent);
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;

                var sourceReference = iterator.objectReferenceValue;
                if (sourceReference == null) continue;
                if (!sourceToDestinationObjectMap.TryGetValue(sourceReference, out var destinationReference)) continue;
                iterator.objectReferenceValue = destinationReference;
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static IEnumerable<Component> GetNonTransformComponents(GameObject gameObject)
        {
            return gameObject.GetComponents<Component>().Where(x => !(x is Transform));
        }

        private static void RemoveExtraComponents(IReadOnlyList<Component> components)
        {
            for (var i = components.Count - 1; i >= 0; i--)
            {
                var component = components[i];
                if (component == null) continue;
                Object.DestroyImmediate(component);
            }
        }

        private static Dictionary<uint, string> BuildEidPaths(IDictionary<uint, GameObject> eidMap, GameObject root)
        {
            return eidMap.ToDictionary(
                x => x.Key,
                x => AnimationUtility.CalculateTransformPath(x.Value.transform, root.transform)
            );
        }

        private static void CleanupPreparedPrefab(PreparedPrefab preparedPrefab)
        {
            if (preparedPrefab.RootLoadedFromPrefabContents)
            {
                PrefabUtility.UnloadPrefabContents(preparedPrefab.Root);
                if (preparedPrefab.GeneratedRoot != null)
                {
                    Object.DestroyImmediate(preparedPrefab.GeneratedRoot);
                }

                return;
            }

            if (preparedPrefab.Root != null)
            {
                Object.DestroyImmediate(preparedPrefab.Root);
            }
        }

        private static void SaveMetaPrefab(PathGetter pathGetter, ILoader layoutLoader, uint hash, Object[] assets, IDictionary<uint, string> eidPaths, string layoutFingerprint)
        {
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(pathGetter.PrefabSavePath);
            var metaGameObject = new GameObject(layoutLoader.LayoutInfo.Name);
            var nestedRoot = (GameObject) PrefabUtility.InstantiatePrefab(prefabAsset);
            nestedRoot.transform.SetParent(metaGameObject.transform, false);

            var prefabMeta = metaGameObject.AddComponent<PrefabMeta>();
            prefabMeta.hash = hash;
            prefabMeta.root = nestedRoot;
            prefabMeta.assets = assets.OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal).ToArray();
            prefabMeta.userData = layoutLoader.LayoutInfo.UserData
                .Where(x => x.Key != ImportFingerprintSession.LayoutKey)
                .Select(x => new MetaUserData { key = x.Key, value = x.Value })
                .Concat(new[] { new MetaUserData { key = ImportFingerprintSession.LayoutKey, value = layoutFingerprint } }).ToArray();
            // Meta prefab 重新记录一份 eid -> 节点映射，供下次导入继续复用旧 fileID。
            prefabMeta.eidToObjects = eidPaths
                .Select(x => new EidToObject
                {
                    eid = x.Key,
                    gameObject = FindGameObjectByPath(nestedRoot, x.Value)
                })
                .Where(x => x.gameObject != null)
                .ToArray();

            PrefabUtility.SaveAsPrefabAsset(metaGameObject, pathGetter.MetaSavePath);
            Object.DestroyImmediate(metaGameObject);
        }

        private static GameObject FindGameObjectByPath(GameObject root, string path)
        {
            if (root == null) return null;
            if (string.IsNullOrWhiteSpace(path)) return root;
            var transform = root.transform.Find(path);
            return transform != null ? transform.gameObject : null;
        }
    }

    // Source identities are strong digests. Unchanged native import revisions reuse
    // verified file digests from the disposable Library cache.
    public sealed class ImportFingerprintSession
    {
        public const string AssetKey = "akyui_asset_sha256_v2";
        public const string OutputKey = "akyui_output_sha256_v2";
        public const string ProcessedKey = "akyui_processed_hash_v2";
        public const string LayoutKey = "akyui_layout_sha256_v2";
        private readonly ILoader _loader;
        private readonly IImportSettings _settings;
        private readonly IPathGetter _paths;
        private readonly string _config;
        private readonly Dictionary<IAsset, string> _fingerprints = new Dictionary<IAsset, string>();
        private readonly Dictionary<string, byte[]> _bytes = new Dictionary<string, byte[]>();
        private readonly Dictionary<IAsset, Dictionary<string, object>> _previous = new Dictionary<IAsset, Dictionary<string, object>>();
        private readonly string m_ConfigDetails;

        public ImportFingerprintSession(IImportSettings settings, ILoader loader, IPathGetter paths)
        {
            _settings = settings;
            _loader = loader;
            _paths = paths;
            var config = new List<string>
            {
                "UniXDFingerprint-v2", Application.unityVersion, EditorUserBuildSettings.activeBuildTarget.ToString(),
                PlayerSettings.colorSpace.ToString(), settings.SpriteSaveScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                settings.AssetOutputDirectoryPath, settings.FontDirectoryPath, settings.PrefabOutputPath, settings.MetaOutputPath
            };
            var visited = new HashSet<Object>();
            config.Add(Snapshot(settings as Object, visited, true));
            if (settings is IPrefabAppearanceSettings appearance) config.Add(appearance.GetAppearanceFingerprint());
            config.Add(Snapshot(settings.TexturePreset, visited));
            foreach (var trigger in settings.Triggers) config.Add(Snapshot(trigger as Object, visited));
            if (AssetDatabase.IsValidFolder(settings.FontDirectoryPath))
                foreach (var guid in AssetDatabase.FindAssets("t:Font", new[] { settings.FontDirectoryPath }).OrderBy(x => x, StringComparer.Ordinal))
                    config.Add(guid + ":" + AssetDatabase.GetAssetDependencyHash(AssetDatabase.GUIDToAssetPath(guid)));
            m_ConfigDetails = string.Join("\n", config);
            _config = Hash(System.Text.Encoding.UTF8.GetBytes(m_ConfigDetails));
        }

        public static string Hash(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(bytes ?? throw new InvalidDataException("Asset source is null.")));
        }

        internal static string ObjectFingerprint(Object value) => Snapshot(value, new HashSet<Object>());

        private static string Snapshot(Object value, HashSet<Object> visited, bool settings = false)
        {
            if (value == null) return "null";
            var path = AssetDatabase.GetAssetPath(value);
            var identity = value.GetType().FullName + ":" + path;
            if (!visited.Add(value)) return identity;
            // Native references are identified by GUID, local file ID and their dependency hash.
            if (!(value is UnityEngine.ScriptableObject) && !(value is Preset))
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId);
                return identity + ":" + guid + ":" + localId + ":" + AssetDatabase.GetAssetDependencyHash(path);
            }
            var json = EditorJsonUtility.ToJson(value);
            if (settings)
                json = System.Text.RegularExpressions.Regex.Replace(json,
                    "\\\"(?:reimportLayout|reimportAsset|logType)\\\"\\s*:\\s*(?:true|false|-?\\d+)\\s*,?", "");
            json = System.Text.RegularExpressions.Regex.Replace(json, "\\\"instanceID\\\"\\s*:\\s*(-?\\d+)", match =>
            {
                var referenced = EditorUtility.EntityIdToObject(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                return "\"reference\":" + JsonSerializer.ToJsonString(Snapshot(referenced, visited));
            });
            return identity + ":" + json;
        }

        public byte[] LoadAsset(string name)
        {
            if (!_bytes.TryGetValue(name, out var bytes)) _bytes[name] = bytes = _loader.LoadAsset(name);
            return (byte[]) bytes.Clone();
        }

        public string GetFingerprint(IAsset asset)
        {
            if (_fingerprints.TryGetValue(asset, out var fingerprint)) return fingerprint;
            var source = _loader is IAssetFingerprintProvider provider
                ? provider.GetAssetSourceFingerprint(asset.FileName) : Hash(LoadAsset(asset.FileName));
            var sprite = asset as SpriteAsset;
            var vector = asset as VectorSpriteAsset;
            var descriptor = vector != null ? JsonSerializer.ToJsonString(new
            {
                Type = asset.GetType().FullName, asset.FileName, asset.UserData,
                Width = vector.Size.x, Height = vector.Size.y, vector.PreserveViewport
            }) : JsonSerializer.ToJsonString(new
            {
                Type = asset.GetType().FullName, asset.FileName, asset.UserData,
                Width = sprite?.Size.x, Height = sprite?.Size.y,
                Border = sprite?.Border == null ? null : new[] { sprite.Border.Top, sprite.Border.Right, sprite.Border.Bottom, sprite.Border.Left }
            });
            return _fingerprints[asset] = Hash(System.Text.Encoding.UTF8.GetBytes(source + "\n" + _config + "\n" + descriptor));
        }

        public bool TryGetPrevious(IAsset asset, out Dictionary<string, object> data)
        {
            if (_previous.TryGetValue(asset, out data)) return data != null;
            var path = Importer.NormalizeAssetPath(Path.Combine(_paths.AssetOutputDirectoryPath, asset.FileName));
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", path);
            data = null;
            var importer = AssetImporter.GetAtPath(path);
            var configured = asset is VectorSpriteAsset vector
                ? importer is Unity.VectorGraphics.Editor.SVGImporter svg && svg.SvgType == Unity.VectorGraphics.Editor.SVGType.UISVGImage &&
                    svg.ViewportOptions == (vector.PreserveViewport ? Unity.VectorGraphics.ViewportOptions.PreserveViewport : Unity.VectorGraphics.ViewportOptions.DontPreserve) &&
                    !svg.PreserveSVGImageAspect && !svg.GeneratePhysicsShape
                : importer is TextureImporter texture && texture.textureType == TextureImporterType.Sprite && texture.spriteImportMode == SpriteImportMode.Single;
            if (configured && importer != null &&
                File.Exists(fullPath) && !string.IsNullOrEmpty(importer.userData) && AssetDatabase.LoadAssetAtPath<Sprite>(path) != null)
            {
                try
                {
                    var candidate = JsonSerializer.Deserialize<Dictionary<string, object>>(importer.userData);
                    if (candidate != null && candidate.TryGetValue(AssetKey, out var previousFingerprint) &&
                        Equals(previousFingerprint, GetFingerprint(asset)) && candidate.TryGetValue(OutputKey, out var previousOutput) &&
                        Equals(previousOutput, ImportContentCache.GetFileHash(fullPath))) data = candidate;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Missing/legacy/malformed metadata is a cache miss, never a reason to skip.
                }
            }
            _previous[asset] = data;
            return data != null;
        }

        public string GetLayoutFingerprint(Object[] importedAssets)
        {
            var layoutBytes = JsonSerializer.Serialize(LayoutCompressor.ToSerializable(_loader.LayoutInfo));
            var entries = new List<string> { _config, Hash(layoutBytes) };
            // Asset generation may finish in a different order. Its set and mappings define the layout, not enumeration order.
            entries.AddRange(importedAssets.Select(AssetDatabase.GetAssetPath).OrderBy(path => path, StringComparer.Ordinal));
            foreach (var asset in _loader.AssetsInfo.Assets.OrderBy(asset => asset.FileName, StringComparer.Ordinal))
            {
                var name = asset.FileName;
                foreach (var trigger in _settings.Triggers)
                {
                    var mapped = trigger.OnLoadAsset(name);
                    if (!string.IsNullOrWhiteSpace(mapped)) name = mapped;
                }
                entries.Add(asset.FileName + "\n" + name);
            }
            if (File.Exists("Library/UniXD/trace-fingerprints"))
            {
                var directory = Path.Combine("Library/UniXD/FingerprintTrace", _loader.LayoutInfo.Name);
                Directory.CreateDirectory(directory);
                void Trace(string name, string content)
                {
                    var current = Path.Combine(directory, name);
                    if (File.Exists(current)) File.Copy(current, current + ".previous", true);
                    File.WriteAllText(current, content);
                }
                Trace("config.txt", m_ConfigDetails);
                Trace("layout.json", System.Text.Encoding.UTF8.GetString(layoutBytes));
                Trace("inputs.txt", string.Join("\n", entries));
            }
            return Hash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", entries)));
        }

        public static ulong ProcessedHash(byte[] bytes, Border border)
        {
            return unchecked(FastHash.CalculateHash(bytes) + (border == null ? FastHash.CalculateHash("NoBorder") : FastHash.CalculateHash($"Border{border.ToVector4()}")));
        }
    }

    public class EditorAssetLoader : IAssetLoader, IVectorAssetLoader
    {
        private readonly PathGetter _pathGetter;
        private readonly ImportLogger _logger;
        private readonly IImportTrigger[] _triggers;

        public EditorAssetLoader(PathGetter pathGetter, ImportLogger logger, IImportTrigger[] triggers)
        {
            _pathGetter = pathGetter;
            _logger = logger;
            _triggers = triggers;
        }

        private string ConvertName(string fileName)
        {
            foreach (var trigger in _triggers)
            {
                var newName = trigger.OnLoadAsset(fileName);
                if (!string.IsNullOrWhiteSpace(newName)) fileName = newName;
            }
            return fileName;
        }

        internal string ResolveAssetPath(string fileName)
        {
            var convertedName = ConvertName(fileName)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(convertedName)) return convertedName;
            if (convertedName.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return convertedName;
            if (convertedName.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return convertedName;
            return Path.Combine(_pathGetter.AssetOutputDirectoryPath, convertedName).Replace('\\', '/');
        }

        public Sprite LoadSprite(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(ResolveAssetPath(name));
        }

        public Material LoadVectorMaterial(string name)
        {
            var path = ResolveAssetPath(name);
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var graphic = source == null ? null : source.GetComponentInChildren<UnityEngine.UI.Graphic>(true);
            return graphic != null ? graphic.material : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().FirstOrDefault();
        }

        public Font LoadFont(string name)
        {
            var pathWithoutExtension = Path.Combine(_pathGetter.FontDirectoryPath, name);

            var ttf = AssetDatabase.LoadAssetAtPath<Font>(pathWithoutExtension + ".ttf");
            if (ttf != null) return ttf;

            var otf = AssetDatabase.LoadAssetAtPath<Font>(pathWithoutExtension + ".otf");
            if (otf != null) return otf;

            _logger.Warning($"Font {pathWithoutExtension} is not found");
            return null;
        }

        public Dictionary<string, object> LoadMeta(string name)
        {
            var importer = AssetImporter.GetAtPath(ResolveAssetPath(name));
            return JsonSerializer.Deserialize<Dictionary<string, object>>(importer.userData);
        }
    }

    public interface IPathGetter
    {
        string AssetOutputDirectoryPath { get; }
        string PrefabSavePath { get; }
        string MetaSavePath { get; }
        string FontDirectoryPath { get; }
    }

    public class PathGetter : IPathGetter
    {
        public string AssetOutputDirectoryPath { get; }
        public string PrefabSavePath { get; }
        public string MetaSavePath { get; }
        public string FontDirectoryPath { get; }

        public PathGetter(IImportSettings settings, string fileName)
        {
            var assetOutputDirectoryPath = settings.AssetOutputDirectoryPath.Replace("{name}", fileName);
            if (!assetOutputDirectoryPath.EndsWith("/")) assetOutputDirectoryPath += "/";
            AssetOutputDirectoryPath = assetOutputDirectoryPath;

            PrefabSavePath = settings.PrefabOutputPath.Replace("{name}", fileName) + ".prefab";
            MetaSavePath = settings.MetaOutputPath.Replace("{name}", fileName) + ".prefab";
            FontDirectoryPath = settings.FontDirectoryPath;
        }
    }
}
