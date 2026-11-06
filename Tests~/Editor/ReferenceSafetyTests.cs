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
        [TearDown] public void TearDown() { AssetDatabase.DeleteAsset(m_Root); }
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
}
