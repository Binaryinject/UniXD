using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
namespace BIEngine.UniXD.Editor {
    // Asset Store distributions do not expose a UPM version; detect their assembly definitions as well.
    [InitializeOnLoad]
    public sealed class OptionalIntegrationDefines : AssetPostprocessor, IActiveBuildTargetChanged {
        private static bool m_Scheduled;
        static OptionalIntegrationDefines() => Schedule();
        public int callbackOrder => 0;
        public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget) => Schedule();
        private static void Schedule() {
            if (m_Scheduled) return;
            m_Scheduled = true;
            EditorApplication.delayCall += () => { m_Scheduled = false; Refresh(); };
        }
        private static bool HasAssembly(string name) {
            var path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(name);
            return !string.IsNullOrEmpty(path) && AssetDatabase.LoadAssetAtPath<UnityEditorInternal.AssemblyDefinitionAsset>(path) != null;
        }
        public static void Refresh() {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            if (group == BuildTargetGroup.Unknown) return;
            var target = NamedBuildTarget.FromBuildTargetGroup(group);
            var original = PlayerSettings.GetScriptingDefineSymbols(target);
            var symbols = original.Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            void Set(string symbol, bool enabled) {
                if (enabled) { if (!symbols.Contains(symbol)) symbols.Add(symbol); }
                else symbols.RemoveAll(s => s == symbol);
            }
            Set("UNIXD_OSA_SUPPORT", HasAssembly("OSA.Core"));
            Set("UNIXD_PRIMETWEEN_SUPPORT", HasAssembly("PrimeTween.Runtime"));
            var updated = string.Join(";", symbols);
            if (updated != original) PlayerSettings.SetScriptingDefineSymbols(target, updated);
        }
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] from) {
            if (imported.Concat(deleted).Concat(moved).Concat(from).Any(p => p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) || p.EndsWith("packages-lock.json", StringComparison.OrdinalIgnoreCase))) Schedule();
        }
    }
}
