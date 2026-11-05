using System;
using System.Collections.Generic;
using System.Linq;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Loader;
using UnityEngine;

namespace BIEngine.UniXD.CommonTrigger
{
    [CreateAssetMenu(menuName = "UniXD/Triggers/SameSpriteDetect", fileName = nameof(SameSpriteDetectTrigger))]
    public class SameSpriteDetectTrigger : ImportTrigger
    {
        // Only deduplicates the current import. Global libraries belong to SharedSpriteResourceTrigger.
        private readonly Dictionary<string, string> m_Map = new Dictionary<string, string>();

        public override void OnPreprocessAllAssets(ILoader layoutLoader, ref List<IAsset> assets)
        {
            m_Map.Clear();

            var removed = new HashSet<IAsset>();
            var cache = new Dictionary<ulong, IAsset>();
            var spriteHashCache = new Dictionary<string, ulong>();
            foreach (var asset in assets)
            {
                Importer.CurrentProgress?.Update($"检查图片 {asset.FileName}", 0f);
                if (!TryGetSpriteHash(layoutLoader, asset, spriteHashCache, out var hash)) continue;
                if (!cache.TryGetValue(hash, out var hit))
                {
                    cache.Add(hash, asset);
                    continue;
                }
                removed.Add(asset);
                m_Map[asset.FileName] = hit.FileName;
            }

            assets.RemoveAll(removed.Contains);

            if (removed.Count > 0)
            {
                Debug.Log($"[SameSpriteDetect] Remove {removed.Count} assets");
            }
        }

        public override string OnLoadAsset(string fileName) => m_Map.TryGetValue(fileName, out var mapped) ? mapped : fileName;

        private static ulong CreateSpriteHash(byte[] bytes, Border border)
        {
            return FastHash.CalculateHash(bytes)
                   + (border == null ? FastHash.CalculateHash("NoBorder") : FastHash.CalculateHash($"Border{border.ToVector4()}"));
        }

        private static bool TryGetSpriteHash(ILoader layoutLoader, IAsset asset, Dictionary<string, ulong> spriteHashCache, out ulong hash)
        {
            hash = default;
            if (!(asset is SpriteAsset spriteAsset)) return false;

            if (spriteHashCache.TryGetValue(spriteAsset.FileName, out hash)) return true;

            var fingerprints = Importer.Fingerprints;
            if (fingerprints != null && !Importer.Settings.ReimportAsset && fingerprints.TryGetPrevious(asset, out var previous) &&
                previous.TryGetValue(ImportFingerprintSession.ProcessedKey, out var processedHash) &&
                ulong.TryParse(processedHash as string, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out hash))
            {
                spriteHashCache[spriteAsset.FileName] = hash;
                return true;
            }

            var loadAsset = fingerprints != null ? fingerprints.LoadAsset(spriteAsset.FileName) : layoutLoader.LoadAsset(spriteAsset.FileName);
            hash = CreateProcessedSpriteHash(layoutLoader, asset, loadAsset);
            spriteHashCache[spriteAsset.FileName] = hash;
            return true;
        }

        private static ulong CreateProcessedSpriteHash(ILoader layoutLoader, IAsset asset, byte[] bytes)
        {
            var processedBytes = bytes.ToArray();
            var processedAsset = CloneAsset(asset);
            var processedUserData = new Dictionary<string, object>();

            var triggers = Importer.Settings?.Triggers ?? Array.Empty<IImportTrigger>();
            foreach (var trigger in triggers)
            {
                if (trigger is SameSpriteDetectTrigger) continue;
                trigger.OnPreprocessAsset(layoutLoader, ref processedBytes, ref processedAsset, ref processedUserData);
            }

            return CreateSpriteHash(processedBytes, (processedAsset as SpriteAsset)?.Border);
        }

        private static IAsset CloneAsset(IAsset asset)
        {
            if (asset is SpriteAsset spriteAsset)
            {
                return new SpriteAsset(
                    spriteAsset.FileName,
                    spriteAsset.Hash,
                    spriteAsset.Size,
                    spriteAsset.UserData,
                    spriteAsset.Border == null ? null : new Border(spriteAsset.Border.Top, spriteAsset.Border.Right, spriteAsset.Border.Bottom, spriteAsset.Border.Left)
                );
            }

            return asset;
        }
    }
}
