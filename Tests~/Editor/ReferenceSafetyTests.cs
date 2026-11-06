using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using BIEngine.UniXD.CommonTrigger;
using BIEngine.UniXD.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.Tests {
    public sealed class ReferenceSafetyTests {
        private const string m_Root = "Assets/UniXDReferenceSafetyTests";
        private const BindingFlags m_Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [SetUp] public void SetUp() { AssetDatabase.DeleteAsset(m_Root); Directory.CreateDirectory(m_Root + "/Res/Shared"); }
        [TearDown] public void TearDown() { ReferenceSafetySaveHook.onSave = null; ReferenceSafetyImportHook.onImport = null; AssetDatabase.DeleteAsset(m_Root); }
        private static Sprite CreateOwnedSprite() {
            var texture = new Texture2D(4, 4);
            var path = m_Root + "/Res/Shared/sprite.png";
            try { File.WriteAllBytes(path, texture.EncodeToPNG()); }
            finally { Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            var owner = new GameObject("Business", typeof(RectTransform), typeof(Image));
            try { owner.GetComponent<Image>().sprite = sprite; PrefabUtility.SaveAsPrefabAsset(owner, m_Root + "/Business.prefab"); }
            finally { Object.DestroyImmediate(owner); }
            return sprite;
        }
        private static void AssertOwnerIntact(string path) {
            Assert.That(File.Exists(path), Is.True, "A still-referenced image must survive cleanup");
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(m_Root + "/Business.prefab").GetComponent<Image>().sprite, Is.Not.Null);
        }
        [Test] public void SharedCleanupPreservesOwnerAbsentFromIncrementalIndex() {
            var sprite = CreateOwnedSprite(); var path = AssetDatabase.GetAssetPath(sprite);
            var type = typeof(SharedSpriteResourceTrigger); var indexType = type.Assembly.GetType("BIEngine.UniXD.CommonTrigger.SharedSpriteIndex");
            var index = Activator.CreateInstance(indexType, true);
            var imageType = indexType.GetNestedType("Image", BindingFlags.NonPublic);
            var image = Activator.CreateInstance(imageType, true);
            imageType.GetField("guid", m_Flags).SetValue(image, AssetDatabase.AssetPathToGUID(path));
            imageType.GetField("path", m_Flags).SetValue(image, path);
            imageType.GetField("key", m_Flags).SetValue(image, "fixture");
            imageType.GetField("encoded", m_Flags).SetValue(image, "fixture");
            indexType.GetMethod("SetImage", m_Flags).Invoke(index, new[] { image });
            var trigger = ScriptableObject.CreateInstance<SharedSpriteResourceTrigger>();
            try {
                type.GetField("m_Index", m_Flags).SetValue(trigger, index);
                type.GetField("m_Root", m_Flags).SetValue(trigger, m_Root + "/Res");
                type.GetField("m_Shared", m_Flags).SetValue(trigger, m_Root + "/Res/Shared");
                type.GetMethod("PruneUnusedShared", m_Flags).Invoke(trigger, null);
                AssertOwnerIntact(path);
            } finally { Object.DestroyImmediate(trigger); }
        }
        [TestCase(false)]
        [TestCase(true)] public void SharedMigrationDiscoversOwnerAbsentFromIncrementalIndex(bool restoreOldReferenceDuringSave) {
            var sprite = CreateOwnedSprite(); var sourcePath = AssetDatabase.GetAssetPath(sprite);
            var targetPath = m_Root + "/Res/Shared/target.png";
            Assert.That(AssetDatabase.CopyAsset(sourcePath, targetPath), Is.True);
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport);
            var type = typeof(SharedSpriteResourceTrigger);
            var indexType = type.Assembly.GetType("BIEngine.UniXD.CommonTrigger.SharedSpriteIndex");
            var index = Activator.CreateInstance(indexType, true);
            var migration = Activator.CreateInstance(indexType.GetNestedType("Migration", BindingFlags.NonPublic), true);
            migration.GetType().GetField("source", m_Flags).SetValue(migration, AssetDatabase.AssetPathToGUID(sourcePath));
            migration.GetType().GetField("target", m_Flags).SetValue(migration, AssetDatabase.AssetPathToGUID(targetPath));
            ((System.Collections.IList)indexType.GetField("pending", m_Flags).GetValue(index)).Add(migration);
            var trigger = ScriptableObject.CreateInstance<SharedSpriteResourceTrigger>();
            try {
                type.GetField("m_Index", m_Flags).SetValue(trigger, index);
                type.GetField("m_Root", m_Flags).SetValue(trigger, m_Root + "/Res");
                type.GetField("m_Shared", m_Flags).SetValue(trigger, m_Root + "/Res/Shared");
                type.GetField("m_IndexPath", m_Flags).SetValue(trigger, m_Root + "/Res/Shared/.index.json");
                var ownerPath = m_Root + "/Business.prefab";
                var original = File.ReadAllText(ownerPath);
                var targetGuid = AssetDatabase.AssetPathToGUID(targetPath);
                var restored = false;
                if (restoreOldReferenceDuringSave) {
                    var dirtyAsset = new Material(Shader.Find("UI/Default"));
                    AssetDatabase.CreateAsset(dirtyAsset, m_Root + "/DirtyOnImport.mat");
                    ReferenceSafetyImportHook.onImport = paths => {
                        if (Array.IndexOf(paths, ownerPath) < 0 || !File.ReadAllText(ownerPath).Contains(targetGuid)) return;
                        EditorUtility.SetDirty(dirtyAsset);
                    };
                }
                if (restoreOldReferenceDuringSave) ReferenceSafetySaveHook.onSave = paths => {
                    if (!File.ReadAllText(ownerPath).Contains(targetGuid)) return paths;
                    var stamp = File.GetLastWriteTimeUtc(ownerPath);
                    File.WriteAllText(ownerPath, original);
                    File.SetLastWriteTimeUtc(ownerPath, stamp); // Same-length references can share a cached stamp.
                    restored = true;
                    ReferenceSafetySaveHook.onSave = null;
                    return Array.FindAll(paths, path => path != ownerPath);
                };
                if (restoreOldReferenceDuringSave) {
                    var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod("RecoverMigrations", m_Flags).Invoke(trigger, null));
                    Assert.That(error.InnerException, Is.TypeOf<IOException>());
                    Assert.That(restored, Is.True, "The test must exercise a late save");
                    Assert.That(File.Exists(sourcePath), Is.True, "Uncommitted references must keep their source");
                    Assert.That(File.ReadAllText(m_Root + "/Res/Shared/.index.json.journal"), Does.Contain(AssetDatabase.AssetPathToGUID(sourcePath)));
                    ReferenceSafetyImportHook.onImport = null;
                }
                type.GetMethod("RecoverMigrations", m_Flags).Invoke(trigger, null);
                var owner = AssetDatabase.LoadAssetAtPath<GameObject>(m_Root + "/Business.prefab");
                Assert.That(owner.GetComponent<Image>().sprite, Is.Not.Null, "Missing index owners must not cause dangling GUIDs");
                Assert.That(AssetDatabase.GetAssetPath(owner.GetComponent<Image>().sprite), Is.EqualTo(targetPath));
                Assert.That(File.Exists(sourcePath), Is.False);
            } finally { Object.DestroyImmediate(trigger); }
        }
        [Test] public void ArtCleanupPreservesSpriteStillReferencedByGeneratedPrefab() {
            var sprite = CreateOwnedSprite(); var path = AssetDatabase.GetAssetPath(sprite);
            var type = typeof(Importer);
            var index = Activator.CreateInstance(type.Assembly.GetType("BIEngine.UniXD.Editor.AssetReferenceIndex"), true);
            type.GetMethod("DeleteUnusedAssets", m_Flags | BindingFlags.Static).Invoke(null, new object[] {
                new Object[] { sprite }, Array.Empty<Object>(), null, m_Root + "/Res",
                m_Root + "/Business.prefab", m_Root + "/ArtMeta.prefab", index
            });
            AssertOwnerIntact(path);
        }
        [Test] public void ArtCleanupStillDeletesUnreferencedSprite() {
            var sprite = CreateOwnedSprite(); var path = AssetDatabase.GetAssetPath(sprite);
            AssetDatabase.DeleteAsset(m_Root + "/Business.prefab");
            var type = typeof(Importer);
            var index = Activator.CreateInstance(type.Assembly.GetType("BIEngine.UniXD.Editor.AssetReferenceIndex"), true);
            type.GetMethod("DeleteUnusedAssets", m_Flags | BindingFlags.Static).Invoke(null, new object[] {
                new Object[] { sprite }, Array.Empty<Object>(), null, m_Root + "/Res",
                m_Root + "/Art.prefab", m_Root + "/ArtMeta.prefab", index
            });
            Assert.That(File.Exists(path), Is.False);
        }
        [Test] public void ArtCleanupPreservesSpriteCopiedIntoBusinessPrefab() {
            var sprite = CreateOwnedSprite(); var path = AssetDatabase.GetAssetPath(sprite);
            var type = typeof(Importer);
            var index = Activator.CreateInstance(type.Assembly.GetType("BIEngine.UniXD.Editor.AssetReferenceIndex"), true);
            type.GetMethod("DeleteUnusedAssets", m_Flags | BindingFlags.Static).Invoke(null, new object[] {
                new Object[] { sprite }, Array.Empty<Object>(), null, m_Root + "/Res",
                m_Root + "/Art.prefab", m_Root + "/ArtMeta.prefab", index
            });
            AssertOwnerIntact(path);
        }
    }
    internal sealed class ReferenceSafetyImportHook : AssetPostprocessor {
        internal static Action<string[]> onImport;
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) => onImport?.Invoke(imported);
    }
    internal sealed class ReferenceSafetySaveHook : AssetModificationProcessor {
        internal static Func<string[], string[]> onSave;
        private static string[] OnWillSaveAssets(string[] paths) => onSave == null ? paths : onSave(paths);
    }
}
