using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Extensions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
namespace BIEngine.UniXD.Tests {
    public sealed class OptionalIntegrationTests {
        private const string m_Folder = "Assets/UniXDOptionalIntegrationTests";
        private sealed class Paths : IPathGetter {
            public string AssetOutputDirectoryPath => m_Folder;
            public string PrefabSavePath => m_Folder + "/Buttons.prefab";
            public string MetaSavePath => m_Folder + "/Meta.prefab";
            public string FontDirectoryPath => m_Folder;
        }
        [Test] public void TriggerScriptGuidsSurviveMigration() {
            Assert.That(AssetDatabase.GUIDToAssetPath("48032df0710b4b30972a6a09e1f5e646"), Does.EndWith("Core/Extensions/OsaScrollViewExportTrigger.cs"));
            Assert.That(AssetDatabase.GUIDToAssetPath("af70286241ab43088d8232d24f3fe248"), Does.EndWith("Core/Extensions/ButtonFeedbackExportTrigger.cs"));
        }
        [TestCase(false)] [TestCase(true)] public void OsaExportUsesOptionalBackendWithoutChangingOrdinaryLists(bool grid) {
            var trigger = ScriptableObject.CreateInstance<OsaScrollViewExportTrigger>();
            var root = new GameObject("Scroll", typeof(RectTransform));
            var template = new GameObject("Item", typeof(RectTransform)); template.transform.SetParent(root.transform, false);
            ((RectTransform)template.transform).sizeDelta = new Vector2(120, 45);
            var node = new VerticalListComponent(8, 4, 6, null) { generateOsaScrollView = false };
            try {
                Assert.That(trigger.CreateComponent(root, node, null), Is.Null);
                Assert.That(template.transform.parent, Is.EqualTo(root.transform));
                node.generateOsaScrollView = true;
                if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BIEngine.UniXD.Osa.Editor")) {
                    Assert.Throws<ImportException>(() => trigger.CreateComponent(root, node, null));
                    Assert.That(root.transform.childCount, Is.EqualTo(1)); return;
                }
                if (grid) { var fields = new SerializedObject(trigger); fields.FindProperty("m_DefaultAdapterType").enumValueIndex = 1; fields.ApplyModifiedPropertiesWithoutUndo(); }
                var adapter = trigger.CreateComponent(root, node, null);
                Assert.That(adapter, Is.Not.Null);
                Assert.That(adapter.GetType().Name, Does.Contain(grid ? "GridAdapter" : "ListAdapter"));
                Assert.That(root.GetComponent<RectMask2D>(), Is.Not.Null);
                Assert.That(root.transform.Find("Content"), Is.Not.Null);
                Assert.That(root.transform.Find("Templates").gameObject.activeSelf, Is.False);
                Assert.That(template.transform.IsChildOf(root.transform.Find("Templates")), Is.True);
                Assert.That(root.GetComponent<LayoutGroup>(), Is.Null);
                Assert.That(root.GetComponent<ContentSizeFitter>(), Is.Null);
                Assert.That(trigger.PreviewCreatedComponents(node, "Scroll")[0], Is.EqualTo(adapter.GetType().Name));
            } finally { Object.DestroyImmediate(root); Object.DestroyImmediate(trigger); }
        }
        [Test] public void FeedbackExportIsOptionalIdempotentAndPreservesExistingSettings() {
            if (!AssetDatabase.IsValidFolder(m_Folder)) AssetDatabase.CreateFolder("Assets", "UniXDOptionalIntegrationTests");
            var trigger = ScriptableObject.CreateInstance<ButtonFeedbackExportTrigger>();
            var root = new GameObject("Buttons", typeof(RectTransform), typeof(Image), typeof(Button));
            try {
                PrefabUtility.SaveAsPrefabAsset(root, new Paths().PrefabSavePath); Object.DestroyImmediate(root); root = null;
                var preview = new List<string> { "Button" }; trigger.PreviewAdditionalComponents(preview);
                trigger.OnPostprocessFile(null, new Paths());
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(new Paths().PrefabSavePath);
                var feedbacks = prefab.GetComponents<Component>().Where(c => c != null && c.GetType().Name == "ButtonFeedback").ToArray();
                Assert.That(feedbacks.Length, Is.EqualTo(preview.Contains("ButtonFeedback") ? 1 : 0));
                if (feedbacks.Length > 0) {
                    var edit = PrefabUtility.LoadPrefabContents(new Paths().PrefabSavePath);
                    try { var component = edit.GetComponent(feedbacks[0].GetType()); var fields = new SerializedObject(component); fields.FindProperty("m_PressedScale").floatValue = .8f; fields.ApplyModifiedPropertiesWithoutUndo(); PrefabUtility.SaveAsPrefabAsset(edit, new Paths().PrefabSavePath); }
                    finally { PrefabUtility.UnloadPrefabContents(edit); }
                }
                var before = File.ReadAllBytes(new Paths().PrefabSavePath);
                trigger.OnPostprocessFile(null, new Paths());
                Assert.That(File.ReadAllBytes(new Paths().PrefabSavePath), Is.EqualTo(before));
            } finally { if (root != null) Object.DestroyImmediate(root); Object.DestroyImmediate(trigger); AssetDatabase.DeleteAsset(m_Folder); }
        }
    }
}
