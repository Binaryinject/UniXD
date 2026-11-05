using System;
using System.Collections.Generic;
using System.Linq;
using BIEngine.UniXD.Loader;
using Unity.VectorGraphics;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    // Shadows are visual siblings, never part of layout bounds or button hit areas.
    internal static class XdShadowRenderer
    {
        internal static bool HasShadows(XdObjectJson node)
        {
            if (node.Visible == false) return false;
            return (node.Style?.Filters ?? Array.Empty<XdFilterJson>()).Any(x =>
                x.Type == "dropShadow" && (x.Params?.DropShadows?.Length ?? 0) > 0)
                || (node.Group?.Children ?? Array.Empty<XdObjectJson>()).Any(HasShadows);
        }

        internal static IElement[] Create(XdObjectJson node, Obb obb, Obb parent,
            AnchorXType anchorX, AnchorYType anchorY, XdAssetHolder holder, List<IAsset> assets,
            float inheritedOpacity = 1f)
        {
            var shadows = (node.Style?.Filters ?? Array.Empty<XdFilterJson>())
                .Where(x => x.Type == "dropShadow")
                .SelectMany(x => x.Params?.DropShadows ?? Array.Empty<XdDropShadowJson>())
                .Where(x => x.Color != null && x.Color.ToUnityColor().a > 0f).ToArray();
            if (shadows.Length == 0 || node.Visible == false || obb.Size.x <= 0f || obb.Size.y <= 0f)
                return Array.Empty<IElement>();
            // Text remains a TMP node; this path handles vector button backgrounds.
            if (node.Type != "shape" && !(node.Group != null && SvgGroupParser.CanFlatten(node)))
                return Array.Empty<IElement>();

            var grouped = node.Group != null;
            var svg = SvgUtil.CreateSvg(node, grouped ? obb : null, false, true);
            if (string.IsNullOrEmpty(svg)) return Array.Empty<IElement>();
            var settings = XdImporter.Settings;
            var width = Mathf.Max(1, Mathf.RoundToInt(obb.Size.x * settings.SpriteSaveScale));
            var height = Mathf.Max(1, Mathf.RoundToInt(obb.Size.y * settings.SpriteSaveScale));
            var scale = new Vector2(width / obb.Size.x, height / obb.Size.y);
            var result = new List<IElement>();
            for (var i = 0; i < shadows.Length; i++)
            {
                var shadow = shadows[i];
                var sigma = new Vector2(Mathf.Max(0f, shadow.Radius) * scale.x, Mathf.Max(0f, shadow.Radius) * scale.y);
                // XD visualBounds includes three blur radii on each side. One extra
                // transparent texel keeps bilinear filtering away from the texture edge.
                var padX = Mathf.CeilToInt(3f * sigma.x) + 1;
                var padY = Mathf.CeilToInt(3f * sigma.y) + 1;
                var size = new Vector2((width + 2 * padX) / scale.x, (height + 2 * padY) / scale.y);
                var description = "XD-shadow-v3-raw-alpha\n" + svg + "\n" +
                    Utf8Json.JsonSerializer.ToJsonString(shadow) + "\n" + width + "x" + height;
                var hash = FastHash.CalculateHash(description);
                var file = "Shadow_" + hash.ToString("x8") + ".png";
                var color = shadow.Color.ToUnityColor();
                holder.Save(file, () => Rasterize(SvgToPng.Convert(svg, obb.Size,
                    grouped ? ViewportOptions.PreserveViewport : ViewportOptions.DontPreserve, settings),
                    sigma, padX, padY, color), description);
                if (!assets.Any(x => x.FileName == file))
                    assets.Add(new SpriteAsset(file, hash, size, null, new Border(0, 0, 0, 0)));
                var angle = obb.Rotation * Mathf.Deg2Rad;
                var offset = new Vector2(shadow.Dx * Mathf.Cos(angle) - shadow.Dy * Mathf.Sin(angle),
                    shadow.Dx * Mathf.Sin(angle) + shadow.Dy * Mathf.Cos(angle));
                var position = obb.CalcLocalRect().center - (parent?.Size ?? Vector2.zero) / 2f + offset;
                var eid = FastHash.CalculateHash((node.Guid ?? node.Id) + "/__XDShadow/" + i);
                var element = new ObjectElement(eid, "__XDShadow_" + (node.Guid ?? node.Id) + "_" + i,
                    position, size, anchorX, anchorY, obb.Rotation, node.Visible ?? true,
                    new IComponent[] { new ImageComponent(file,
                        new Color(1f, 1f, 1f, inheritedOpacity * (node.Style?.Opacity ?? 1f)), Vector2Int.one, hash, false) },
                    Array.Empty<uint>());
                result.Add(element);
            }
            return result.ToArray();
        }

        internal static byte[] Rasterize(byte[] source, Vector2 sigma, int padX, int padY, Color color)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D output = null;
            try
            {
                if (!texture.LoadImage(source)) throw new InvalidOperationException("Cannot decode XD shadow source.");
                var width = texture.width + 2 * padX;
                var height = texture.height + 2 * padY;
                var alpha = new float[width * height];
                var pixels = texture.GetPixels32();
                for (var y = 0; y < texture.height; y++)
                    for (var x = 0; x < texture.width; x++)
                        alpha[(y + padY) * width + x + padX] = pixels[y * texture.width + x].a / 255f;
                alpha = Blur(alpha, width, height, sigma.x, true);
                alpha = Blur(alpha, width, height, sigma.y, false);
                var result = new Color32[alpha.Length];
                var rgb = (Color32) color;
                for (var i = 0; i < result.Length; i++)
                {
                    var coverage = Mathf.Clamp01(alpha[i] * color.a);
                    result[i] = new Color32(rgb.r, rgb.g, rgb.b,
                        (byte) Mathf.RoundToInt(coverage * 255f));
                }
                output = new Texture2D(width, height, TextureFormat.RGBA32, false);
                output.SetPixels32(result);
                output.Apply();
                return output.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                if (output != null) UnityEngine.Object.DestroyImmediate(output);
            }
        }

        private static float[] Blur(float[] source, int width, int height, float sigma, bool horizontal)
        {
            if (sigma < 0.001f) return source;
            var radius = Mathf.CeilToInt(3f * sigma);
            var kernel = new float[2 * radius + 1];
            var total = 0f;
            for (var i = -radius; i <= radius; i++)
            {
                var weight = Mathf.Exp(-(float) i * i / (2f * sigma * sigma));
                kernel[i + radius] = weight;
                total += weight;
            }
            for (var i = 0; i < kernel.Length; i++) kernel[i] /= total;
            var result = new float[source.Length];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var value = 0f;
                    var coordinate = horizontal ? x : y;
                    var length = horizontal ? width : height;
                    for (var k = Math.Max(-radius, -coordinate); k <= Math.Min(radius, length - 1 - coordinate); k++)
                        value += source[y * width + x + (horizontal ? k : k * width)] * kernel[k + radius];
                    result[y * width + x] = value;
                }
            return result;
        }
    }
}
