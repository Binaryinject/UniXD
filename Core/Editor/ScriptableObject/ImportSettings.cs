using System.Linq;
using UnityEditor.Presets;
using UnityEngine;

namespace BIEngine.UniXD.Editor.ScriptableObject
{
    public interface IPrefabAppearanceSettings
    {
        void ApplyAppearance(GameObject root, IPathGetter paths);
        string GetAppearanceFingerprint();
    }

    [CreateAssetMenu(menuName = "UniXD/ImportSettings", fileName = "ImportSettings")]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "AkyuiUnity.Editor.ScriptableObject", "AkyuiUnity.Editor", "AkyuiImportSettings")]
    public class ImportSettings : UnityEngine.ScriptableObject, IImportSettings
    {
        public string PrefabOutputPath => prefabOutputPath;
        [SerializeField] private string prefabOutputPath = "Assets/{name}";

        public string AssetOutputDirectoryPath => assetOutputDirectoryPath;
        [SerializeField] private string assetOutputDirectoryPath = "Assets/{name}/";

        public string MetaOutputPath => metaOutputPath;
        [SerializeField] private string metaOutputPath = "Assets/{name}Meta";

        public string FontDirectoryPath => fontDirectoryPath;
        [SerializeField] private string fontDirectoryPath = "Assets/Fonts/";

        public Preset TexturePreset => texturePreset;
        [SerializeField] private Preset texturePreset = default;

        public IImportTrigger[] Triggers => triggers?.Cast<IImportTrigger>().Where(x => x != null).ToArray() ?? new IImportTrigger[] { };
        [SerializeField] private ImportTrigger[] triggers = default;

        public float SpriteSaveScale => spriteSaveScale;
        [SerializeField] private float spriteSaveScale = 1.0f;

        public bool ReimportLayout => reimportLayout;
        [SerializeField] private bool reimportLayout = false;

        public bool ReimportAsset => reimportAsset;
        [SerializeField] private bool reimportAsset = false;

        public ImportLogType LogType => logType;
        [SerializeField] private ImportLogType logType = ImportLogType.Default;
    }

    public interface IImportSettings
    {
        string PrefabOutputPath { get; }
        string AssetOutputDirectoryPath { get; }
        string MetaOutputPath { get; }
        string FontDirectoryPath { get; }
        Preset TexturePreset { get; }
        IImportTrigger[] Triggers { get; }
        float SpriteSaveScale { get; }
        bool ReimportAsset { get; }
        bool ReimportLayout { get; }
        ImportLogType LogType { get; }
    }
}
