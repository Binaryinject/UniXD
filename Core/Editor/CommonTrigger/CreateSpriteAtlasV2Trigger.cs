using System.IO;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Loader;
using UnityEditor;
using UnityEditor.Presets;
using UnityEditor.U2D;
using UnityEngine;

namespace BIEngine.UniXD.CommonTrigger
{
    [CreateAssetMenu(menuName = "UniXD/Triggers/CreateSpriteAtlasV2", fileName = nameof(CreateSpriteAtlasV2Trigger))]
    public class CreateSpriteAtlasV2Trigger : ImportTrigger
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
            var spriteAtlasPath = spriteAtlasOutputPath.Replace("{name}", loader.LayoutInfo.Name) + ".spriteatlasv2";
            var signature = ImportAtlasCache.Signature(importAssets, spriteAtlasPreset, m_MaxSpriteArea, m_StandaloneTextureCompression, m_StandaloneCompressionQuality);
            if (Importer.Settings?.ReimportAsset != true && ImportContentCache.ArtifactCurrent(spriteAtlasPath, signature)) return;
            var spriteAtlas = new SpriteAtlasAsset
            {
                name = Path.GetFileNameWithoutExtension(spriteAtlasPath)
            };

            var texturePolicy = SpriteAtlasAreaFilter.PrepareTexturePolicy(importAssets, m_MaxSpriteArea);
            texturePolicy.RestorePackedSources();
            spriteAtlas.Add(texturePolicy.GetPackables());
            SpriteAtlasAsset.Save(spriteAtlas, spriteAtlasPath);
            // Update dependencies before compressing any former atlas members.
            AssetDatabase.ImportAsset(spriteAtlasPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(spriteAtlasPath);
            if (spriteAtlasPreset != null)
            {
                spriteAtlasPreset.ApplyTo(importer);
                importer.SaveAndReimport();
            }
            texturePolicy.ApplyStandaloneCompression(m_StandaloneTextureCompression, m_StandaloneCompressionQuality);
            // Texture policy can reimport sources; record their final revisions.
            ImportContentCache.RememberArtifact(spriteAtlasPath, ImportAtlasCache.Signature(importAssets, spriteAtlasPreset,
                m_MaxSpriteArea, m_StandaloneTextureCompression, m_StandaloneCompressionQuality));
        }
    }
}
