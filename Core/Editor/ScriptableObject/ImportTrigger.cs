using System.Collections.Generic;
using BIEngine.UniXD.Generator;
using BIEngine.UniXD.Generator.InternalTrigger;
using BIEngine.UniXD.Loader;
using UnityEditor;
using UnityEngine;

namespace BIEngine.UniXD.Editor.ScriptableObject
{
    public interface IImportTrigger : IGenerateTrigger
    {
        void OnPreprocessAllAssets(ILoader loader, ref List<IAsset> assets);
        void OnPreprocessAsset(ILoader loader, ref byte[] bytes, ref IAsset asset, ref Dictionary<string, object> userData);
        void OnUnityPreprocessAsset(AssetImporter assetImporter, IAsset asset, ref Dictionary<string, object> userData);
        void OnPostprocessAllAssets(ILoader loader, Object[] importAssets);

        string OnLoadAsset(string fileName);

        void OnPostprocessPrefab(ILoader loader, ref GameObject prefab);
        void OnPostprocessFile(ILoader loader, IPathGetter pathGetter);
    }

    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "AkyuiUnity.Editor.ScriptableObject", "AkyuiUnity.Editor", "AkyuiImportTrigger")]
    public abstract class ImportTrigger : UnityEngine.ScriptableObject, IImportTrigger
    {
        public virtual void OnImportCompleted(ILoader loader, IPathGetter paths) { }
        public virtual void OnImportBatchCompleted(IImportSettings settings, bool succeeded) { }
        public virtual void OnPreprocessAllAssets(ILoader loader, ref List<IAsset> assets) { }
        public virtual void OnPreprocessAsset(ILoader loader, ref byte[] bytes, ref IAsset asset, ref Dictionary<string, object> userData) { }
        public virtual void OnUnityPreprocessAsset(AssetImporter assetImporter, IAsset asset, ref Dictionary<string, object> userData) { }
        public virtual void OnPostprocessAllAssets(ILoader loader, Object[] importAssets) { }

        public virtual Component CreateComponent(GameObject gameObject, IComponent component, IAssetLoader assetLoader) => null;
        public virtual string[] PreviewCreatedComponents(IComponent component, string hierarchyPath) => null;
        public virtual void PreviewAdditionalComponents(ICollection<string> components) { }
        public virtual void OnPostprocessComponent(GameObject gameObject, IComponent component) { }

        public virtual string OnLoadAsset(string fileName) => null;

        public virtual void OnPostprocessPrefab(ILoader loader, ref GameObject prefab) { }
        public virtual void OnPostprocessFile(ILoader loader, IPathGetter pathGetter) { }
    }
}
