using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.CommonTrigger
{
    internal static class SpriteAtlasAreaFilter
    {
        internal const int defaultMaxSpriteArea = 2048 * 2048 / 3;

        internal static Object[] Filter(Object[] assets, int maxSpriteArea)
        {
            if (assets == null) return Array.Empty<Object>();

            return assets.Where(asset => asset != null && !IsVectorAsset(asset)
                && (maxSpriteArea <= 0 || GetArea(asset) <= maxSpriteArea)).ToArray();
        }

        internal static TexturePolicyPlan PrepareTexturePolicy(Object[] assets, int maxSpriteArea)
        {
            return new TexturePolicyPlan(assets ?? Array.Empty<Object>(), maxSpriteArea);
        }

        internal sealed class TexturePolicyPlan
        {
            private readonly Object[] m_Assets;
            private readonly AssetReference[] m_References;
            private readonly bool[] m_Packed;
            private readonly Dictionary<string, bool> m_PackedTexturePaths = new Dictionary<string, bool>(StringComparer.Ordinal);

            internal TexturePolicyPlan(Object[] assets, int maxSpriteArea)
            {
                m_Assets = assets;
                m_References = new AssetReference[assets.Length];
                m_Packed = new bool[assets.Length];
                for (var i = 0; i < assets.Length; i++)
                {
                    var asset = assets[i];
                    m_References[i] = new AssetReference(asset);
                    if (asset == null) continue;
                    m_Packed[i] = !IsVectorAsset(asset) && (maxSpriteArea <= 0 || GetArea(asset) <= maxSpriteArea);
                    var path = m_References[i].path;
                    if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is TextureImporter)) continue;
                    // Compression belongs to the whole source texture. If any of its
                    // Sprite subassets is packed, keep the common source uncompressed.
                    m_PackedTexturePaths.TryGetValue(path, out var previouslyPacked);
                    m_PackedTexturePaths[path] = previouslyPacked || m_Packed[i];
                }
            }

            internal void RestorePackedSources()
            {
                foreach (var pair in m_PackedTexturePaths)
                    if (pair.Value) ApplyCompression(pair.Key, TextureImporterCompression.Uncompressed, null);
                RefreshReferences();
            }

            internal Object[] GetPackables()
            {
                return m_Assets.Where((asset, index) => m_Packed[index] && asset != null).ToArray();
            }

            internal void ApplyStandaloneCompression(TextureImporterCompression compression, int quality)
            {
                foreach (var pair in m_PackedTexturePaths)
                    if (!pair.Value) ApplyCompression(pair.Key, compression, Mathf.Clamp(quality, 0, 100));
                RefreshReferences();
            }

            private void RefreshReferences()
            {
                var reloadedAssets = new Dictionary<string, Object[]>(StringComparer.Ordinal);
                // Importer also uses the original complete array after these triggers.
                // Refresh native objects in place without filtering, reordering, or
                // replacing an individual Sprite by its whole Texture2D.
                for (var i = 0; i < m_References.Length; i++)
                    m_Assets[i] = m_References[i].Reload(reloadedAssets);
            }
        }

        private sealed class AssetReference
        {
            internal readonly string path;
            private readonly Object m_Original;
            private readonly long m_LocalId;
            private readonly bool m_HasLocalId;

            internal AssetReference(Object asset)
            {
                m_Original = asset;
                if (asset == null) return;
                path = AssetDatabase.GetAssetPath(asset);
                m_HasLocalId = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out m_LocalId);
            }

            internal Object Reload(Dictionary<string, Object[]> cache)
            {
                if (string.IsNullOrEmpty(path) || !m_HasLocalId) return m_Original;
                if (!cache.TryGetValue(path, out var assets))
                {
                    assets = AssetDatabase.LoadAllAssetsAtPath(path);
                    cache[path] = assets;
                }
                foreach (var asset in assets)
                    if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long localId) && localId == m_LocalId)
                        return asset;
                return null;
            }
        }

        private static void ApplyCompression(string path, TextureImporterCompression compression, int? quality)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            var defaults = importer.GetDefaultPlatformTextureSettings();
            var platforms = new HashSet<string>(StringComparer.Ordinal) { defaults.name };
            // TextureImporter exposes per-platform Get/Set APIs, but no public list
            // of configured names. Read only the names from its serialized array.
            using (var serialized = new SerializedObject(importer))
            {
                var settings = serialized.FindProperty("m_PlatformSettings");
                if (settings != null && settings.isArray)
                    for (var i = 0; i < settings.arraySize; i++)
                    {
                        var name = settings.GetArrayElementAtIndex(i).FindPropertyRelative("m_BuildTarget")?.stringValue;
                        if (!string.IsNullOrEmpty(name)) platforms.Add(name);
                    }
            }

            var dirty = false;
            foreach (var platform in platforms)
            {
                var settings = platform == defaults.name ? defaults : importer.GetPlatformTextureSettings(platform);
                var format = settings.format;
                if (format != TextureImporterFormat.Automatic && ExplicitFormatDefeatsPolicy(format, compression))
                    format = TextureImporterFormat.Automatic;
                if (settings.textureCompression == compression && settings.format == format && !settings.crunchedCompression
                    && (!quality.HasValue || settings.compressionQuality == quality.Value)) continue;

                settings.textureCompression = compression;
                settings.format = format;
                settings.crunchedCompression = false;
                if (quality.HasValue) settings.compressionQuality = quality.Value;
                importer.SetPlatformTextureSettings(settings);
                dirty = true;
            }

            if (dirty) importer.SaveAndReimport();
        }

        private static bool ExplicitFormatDefeatsPolicy(TextureImporterFormat format, TextureImporterCompression compression)
        {
            if (format.ToString().IndexOf("Crunched", StringComparison.Ordinal) >= 0) return true;
            var graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat((TextureFormat)format, false);
            if (graphicsFormat == GraphicsFormat.None) return true;
            var isCompressed = GraphicsFormatUtility.IsCompressedFormat(graphicsFormat);
            return compression == TextureImporterCompression.Uncompressed ? isCompressed : !isCompressed;
        }

        private static bool IsVectorAsset(Object asset) =>
            AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(asset)) is Unity.VectorGraphics.Editor.SVGImporter;

        private static double GetArea(Object asset)
        {
            if (asset is Sprite sprite) return (double)sprite.rect.width * sprite.rect.height;
            if (asset is Texture2D texture) return (double)texture.width * texture.height;
            return 0;
        }
    }
}
