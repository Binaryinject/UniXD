using System.IO;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Loader;
using UnityEditor;
using UnityEditor.Presets;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace BIEngine.UniXD.CommonTrigger
{
    [CreateAssetMenu(menuName = "UniXD/Triggers/CreateSpriteAtlas", fileName = nameof(CreateSpriteAtlasTrigger))]
    public class CreateSpriteAtlasTrigger : ImportTrigger
    {
        [SerializeField] private string spriteAtlasOutputPath = "Assets/{name}SpriteAtlas";
        [SerializeField] private Preset spriteAtlasPreset = default;
        [SerializeField, Min(0), Tooltip("允许加入图集的单张图片最大面积（导入后的宽 × 高，像素²）。超过此值保留为独立图片；0 表示不限制。默认是 2048 × 2048 的三分之一，与图集尺寸设置相互独立。")]
        private int m_MaxSpriteArea = SpriteAtlasAreaFilter.defaultMaxSpriteArea;
        [SerializeField, Tooltip("超过面积限制、作为独立图片使用的纹理压缩设置。")]
        private TextureImporterCompression m_StandaloneTextureCompression = TextureImporterCompression.Compressed;
        [SerializeField, Range(0, 100)] private int m_StandaloneCompressionQuality = 50;

        public override void OnPostprocessAllAssets(ILoader loader, Object[] importAssets)
        {
            var spriteAtlasPath = spriteAtlasOutputPath.Replace("{name}", loader.LayoutInfo.Name) + ".spriteatlas";
            var signature = ImportAtlasCache.Signature(importAssets, spriteAtlasPreset, m_MaxSpriteArea, m_StandaloneTextureCompression, m_StandaloneCompressionQuality);
            if (Importer.Settings?.ReimportAsset != true && ImportContentCache.ArtifactCurrent(spriteAtlasPath, signature)) return;
            var spriteAtlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(spriteAtlasPath);
            if (spriteAtlas == null)
            {
                Debug.Log($"Create SpriteAtlas: {spriteAtlasPath}");

                EditorUtil.CreateDirectory(Path.GetDirectoryName(spriteAtlasPath));
                spriteAtlas = new SpriteAtlas();
                AssetDatabase.CreateAsset(spriteAtlas, spriteAtlasPath);
            }

            if (spriteAtlasPreset != null)
            {
                spriteAtlasPreset.ApplyTo(spriteAtlas);
            }

            var texturePolicy = SpriteAtlasAreaFilter.PrepareTexturePolicy(importAssets, m_MaxSpriteArea);
            texturePolicy.RestorePackedSources();

            spriteAtlas.name = Path.GetFileNameWithoutExtension(spriteAtlasPath);
            spriteAtlas.Remove(spriteAtlas.GetPackables());
            spriteAtlas.Add(texturePolicy.GetPackables());

            // Persist the new membership before reimporting independent textures.
            EditorUtility.SetDirty(spriteAtlas);
            AssetDatabase.SaveAssets();

            SpriteAtlasUtility.PackAtlases(new[] { spriteAtlas }, EditorUserBuildSettings.activeBuildTarget);
            texturePolicy.ApplyStandaloneCompression(m_StandaloneTextureCompression, m_StandaloneCompressionQuality);

            // なにかのバグだと思うんだけど、ここでSaveしないとPresetが反映されないことがある Unity 2020.3.18
            EditorUtility.SetDirty(spriteAtlas);
            AssetDatabase.SaveAssets();
            ImportContentCache.RememberArtifact(spriteAtlasPath, ImportAtlasCache.Signature(importAssets, spriteAtlasPreset,
                m_MaxSpriteArea, m_StandaloneTextureCompression, m_StandaloneCompressionQuality));
        }
    }
}
