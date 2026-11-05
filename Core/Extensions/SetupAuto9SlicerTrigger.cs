using System.Collections.Generic;
using System.IO;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Loader;
using BIEngine.UniXD.Slicing;
using UnityEngine;

namespace BIEngine.UniXD.Extensions
{
    [CreateAssetMenu(menuName = "UniXD/Triggers/SetupAuto9Slicer", fileName = nameof(SetupAuto9SlicerTrigger))]
    public class SetupAuto9SlicerTrigger : ImportTrigger
    {
        [SerializeField] private SliceOptions options;

        // SameSpriteDetect runs this trigger before the actual import. Reuse that work
        // within one artboard, keeping a private copy so other triggers can edit bytes.
        private ILoader _cachedLoader;
        private readonly Dictionary<string, CachedSlice> _cache = new Dictionary<string, CachedSlice>();
        private int _cachedBytes;
        private const int MaxCachedBytes = 32 * 1024 * 1024;

        private sealed class CachedSlice
        {
            public byte[] Bytes;
            public BIEngine.UniXD.Slicing.Border Border;
        }

        public override void OnPostprocessAllAssets(ILoader loader, Object[] importAssets)
        {
            _cache.Clear();
            _cachedBytes = 0;
            _cachedLoader = null;
        }

        public override void OnPreprocessAsset(ILoader loader, ref byte[] bytes, ref IAsset asset, ref Dictionary<string, object> userData)
        {
            if (!(asset is SpriteAsset spriteAsset)) return;

            var extension = Path.GetExtension(asset.FileName);
            if (spriteAsset.Border != null) return;
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg") return;

            if (!ReferenceEquals(_cachedLoader, loader))
            {
                _cache.Clear();
                _cachedBytes = 0;
                _cachedLoader = loader;
            }

            string cacheKey;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                cacheKey = System.Convert.ToBase64String(sha.ComputeHash(bytes));
            cacheKey += $"/{extension}/{options.Tolerate}/{options.CenterSize}/{options.Margin}";
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                bytes = (byte[]) cached.Bytes.Clone();
                spriteAsset.Border = new Border(cached.Border.Top, cached.Border.Right, cached.Border.Bottom, cached.Border.Left);
                return;
            }

            var texture = new Texture2D(2, 2);
            SlicedTexture sliced = null;
            try
            {
                texture.LoadImage(bytes);
                sliced = Slicer.Slice(texture, options);
                if (extension == ".png") bytes = sliced.Texture.EncodeToPNG();
                if (extension == ".jpg" || extension == ".jpeg") bytes = sliced.Texture.EncodeToJPG();

                if (bytes.Length <= MaxCachedBytes - _cachedBytes)
                {
                    _cache[cacheKey] = new CachedSlice { Bytes = (byte[]) bytes.Clone(), Border = sliced.Border };
                    _cachedBytes += bytes.Length;
                }

                spriteAsset.Border = new Border(
                    sliced.Border.Top,
                    sliced.Border.Right,
                    sliced.Border.Bottom,
                    sliced.Border.Left
                );
            }
            finally
            {
                if (sliced != null) Object.DestroyImmediate(sliced.Texture);
                Object.DestroyImmediate(texture);
            }
        }
    }
}
