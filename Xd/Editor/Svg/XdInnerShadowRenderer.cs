using System;
using System.Collections.Generic;
using System.Linq;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Xd.Parsing.Internal;
using Unity.VectorGraphics;
using UnityEngine;

namespace BIEngine.UniXD.Xd {
    internal static class XdInnerShadowRenderer {
        private static readonly Dictionary<string, byte[]> m_Cache = new(StringComparer.Ordinal);
        private static int m_CachedBytes;
        internal static byte[] RenderGroup(string svg, string alphaSvg, Vector2 size, XdImportSettings settings) {
            var bytes = SvgToPng.Convert(svg, size, ViewportOptions.PreserveViewport, settings);
            if (alphaSvg == null) return bytes;
            var color = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var mask = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try {
                color.LoadImage(bytes);
                mask.LoadImage(SvgToPng.Convert(alphaSvg, size, ViewportOptions.PreserveViewport, settings));
                if (color.width != mask.width || color.height != mask.height) throw new InvalidOperationException("Inner shadow mask dimensions differ.");
                var pixels = color.GetPixels32(); var alpha = mask.GetPixels32();
                for (var i = 0; i < pixels.Length; i++) {
                    pixels[i].a = alpha[i].a;
                    if (pixels[i].a == 0) pixels[i] = new Color32(0,0,0,0);
                }
                color.SetPixels32(pixels); color.Apply(); return color.EncodeToPNG();
            } finally {UnityEngine.Object.DestroyImmediate(color);UnityEngine.Object.DestroyImmediate(mask);}
        }
        private static XdDropShadowJson[] Shadows(XdObjectJson node) => (node.Style?.Filters ?? Array.Empty<XdFilterJson>())
            .Where(x => x.Visible != false && (x.Type == "uxdesign#innerShadow" || x.Type == "innerShadow"))
            .SelectMany(x => x.Params?.InnerShadows ?? Array.Empty<XdDropShadowJson>())
            .Where(x => x.Color != null && x.Color.ToUnityColor().a > 0).ToArray();
        internal static bool HasOwnShadow(XdObjectJson node) {
            if (node == null || node.Visible == false || node.Style?.Filters == null) return false;
            foreach (var filter in node.Style.Filters) {
                if (filter.Visible == false || (filter.Type != "uxdesign#innerShadow" && filter.Type != "innerShadow")) continue;
                foreach (var shadow in filter.Params?.InnerShadows ?? Array.Empty<XdDropShadowJson>())
                    if (shadow.Color != null && (shadow.Color.Alpha ?? 1f) > 0) return true;
            }
            return false;
        }
        internal static bool HasShadows(XdObjectJson node) {
            if (node == null || node.Visible == false) return false;
            if (HasOwnShadow(node)) return true;
            foreach (var child in node.Group?.Children ?? Array.Empty<XdObjectJson>()) if (HasShadows(child)) return true;
            return false;
        }
        internal static byte[] Render(string svg, Vector2 size, XdObjectJson node, XdImportSettings settings) {
            var shadows = Shadows(node);
            var key = ImportFingerprintSession.Hash(System.Text.Encoding.UTF8.GetBytes("inner-shadow-v1\n" + svg + "\n" +
                size.ToString("R") + "\n" + settings.SpriteSaveScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                UnityEditor.PlayerSettings.colorSpace + "\n" + Utf8Json.JsonSerializer.ToJsonString(shadows)));
            if (m_Cache.TryGetValue(key, out var cached)) return cached;
            var bytes = Apply(SvgToPng.Convert(svg, size, ViewportOptions.DontPreserve, settings), size, shadows);
            if (bytes.Length <= 16 * 1024 * 1024) {
                if (m_Cache.Count >= 64 || m_CachedBytes + bytes.Length > 16 * 1024 * 1024) {m_Cache.Clear();m_CachedBytes=0;}
                m_Cache[key] = bytes; m_CachedBytes += bytes.Length;
            }
            return bytes;
        }
        internal static byte[] Apply(byte[] source, Vector2 size, XdDropShadowJson[] shadows) {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try {
                if (!texture.LoadImage(source)) throw new InvalidOperationException("Cannot decode inner shadow source.");
                var width = texture.width; var height = texture.height;
                var pixels = texture.GetPixels(); var alpha = pixels.Select(x => x.a).ToArray();
                var scale = new Vector2(width / size.x, height / size.y);
                foreach (var shadow in shadows) {
                    var blurred = XdShadowRenderer.Blur(alpha, width, height, Mathf.Max(0, shadow.Radius) * scale.x, true);
                    blurred = XdShadowRenderer.Blur(blurred, width, height, Mathf.Max(0, shadow.Radius) * scale.y, false);
                    var color = shadow.Color.ToUnityColor();
                    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) {
                        var i = y * width + x;
                        if (alpha[i] == 0) continue;
                        var coverage = 1 - Sample(blurred, width, height, x - shadow.Dx * scale.x, y + shadow.Dy * scale.y);
                        var mixed = Color.Lerp(pixels[i], color, Mathf.Clamp01(coverage * color.a));
                        mixed.a = alpha[i]; // Inner shadow never paints outside the original alpha mask.
                        pixels[i] = mixed;
                    }
                }
                texture.SetPixels(pixels); texture.Apply(); return texture.EncodeToPNG();
            } finally {UnityEngine.Object.DestroyImmediate(texture);}
        }
        private static float Sample(float[] values, int width, int height, float x, float y) {
            var ix = Mathf.FloorToInt(x); var iy = Mathf.FloorToInt(y);
            float At(int px, int py) => px < 0 || py < 0 || px >= width || py >= height ? 0 : values[py * width + px];
            return Mathf.Lerp(Mathf.Lerp(At(ix,iy),At(ix+1,iy),x-ix),Mathf.Lerp(At(ix,iy+1),At(ix+1,iy+1),x-ix),y-iy);
        }
    }
}
