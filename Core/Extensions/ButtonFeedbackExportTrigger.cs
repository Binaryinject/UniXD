using System;
using System.Collections.Generic;
using System.Linq;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Loader;
using UnityEditor;
using UnityEngine;
namespace BIEngine.UniXD.Extensions {
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "EBGame.EditorTools", "EBGame.Editor", "ButtonFeedbackExportTrigger")]
    [CreateAssetMenu(menuName = "UniXD/Triggers/Button Feedback", fileName = "ButtonFeedbackExportTrigger")]
    public sealed class ButtonFeedbackExportTrigger : ImportTrigger {
#if UNIXD_PRIMETWEEN_SUPPORT
        private static readonly Type m_FeedbackType = TypeCache.GetTypesDerivedFrom<Component>().FirstOrDefault(t => t.FullName == "BIEngine.UIPanelStack.ButtonFeedback")
            ?? TypeCache.GetTypesDerivedFrom<Component>().FirstOrDefault(t => t.FullName == "BIEngine.UniXD.ButtonFeedback");
        private static readonly Type[] m_KnownTypes = TypeCache.GetTypesDerivedFrom<Component>().Where(t =>
            t.FullName == "BIEngine.UIPanelStack.ButtonFeedback" || t.FullName == "BIEngine.UniXD.ButtonFeedback").ToArray();
        public override void PreviewAdditionalComponents(ICollection<string> components) {
            if (m_FeedbackType != null && components.Contains("Button")) components.Add("ButtonFeedback");
        }
        public override void OnPostprocessFile(ILoader loader, IPathGetter paths) {
            if (m_FeedbackType == null) return;
            var root = PrefabUtility.LoadPrefabContents(paths.PrefabSavePath);
            try {
                var added = false;
                foreach (var button in root.GetComponentsInChildren<UnityEngine.UI.Button>(true)) {
                    if (m_KnownTypes.Any(t => button.GetComponent(t) != null)) continue;
                    button.gameObject.AddComponent(m_FeedbackType);
                    added = true;
                }
                if (added) PrefabUtility.SaveAsPrefabAsset(root, paths.PrefabSavePath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
#endif
    }
}
