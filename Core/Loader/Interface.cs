using System;
using JetBrains.Annotations;
using UnityEngine;

namespace BIEngine.UniXD.Loader
{
    // Optional: fingerprint the source of generated assets without rasterizing them.
    public interface IAssetFingerprintProvider
    {
        string GetAssetSourceFingerprint(string fileName);
    }

    public interface ILoader : IDisposable
    {
        [NotNull] LayoutInfo LayoutInfo { get; }
        [NotNull] AssetsInfo AssetsInfo { get; }
        [NotNull] byte[] LoadAsset(string fileName);
    }

    public class AssetsInfo
    {
        [NotNull] public readonly IAsset[] Assets;

        public AssetsInfo([NotNull] IAsset[] assets)
        {
            Assets = assets;
        }
    }
}
