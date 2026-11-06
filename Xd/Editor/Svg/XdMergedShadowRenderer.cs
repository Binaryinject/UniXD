using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BIEngine.UniXD.Loader;
using UnityEngine;
using Unity.VectorGraphics;

namespace BIEngine.UniXD.Xd {
    internal static class XdMergedShadowRenderer {
        private sealed class Layer {
            internal ImageComponent image;
            internal Vector2 center, size;
            internal float angle;
            internal bool vector;
        }
        private static Vector2 Rotate(Vector2 value, float angle) {
            var r = angle * Mathf.Deg2Rad;
            return new Vector2(value.x * Mathf.Cos(r) - value.y * Mathf.Sin(r), value.x * Mathf.Sin(r) + value.y * Mathf.Cos(r));
        }
        internal static (Vector2 position, Vector2 size) Merge(Vector2 position, Vector2 size, float rotation,
            List<IComponent> components, List<IElement> shadows, XdAssetHolder holder, List<IAsset> assets, List<IElement> elements) {
            var foreground = components.OfType<ImageComponent>().FirstOrDefault(x => x.Sprite != null);
            if (foreground == null) throw new InvalidOperationException("合并阴影需要合图底图。");
            var layers = shadows.Cast<ObjectElement>().Select(x => new Layer {
                image = x.Components.OfType<ImageComponent>().Single(), center = Rotate(x.Position - position, -rotation),
                size = x.Size, angle = x.Rotation - rotation
            }).ToList();
            layers.Add(new Layer { image = foreground, center = Vector2.zero, size = size, vector = foreground is VectorImageComponent });
            var min = -size / 2; var max = size / 2;
            foreach (var layer in layers)
                foreach (var corner in new[] { new Vector2(-1,-1), new Vector2(-1,1), new Vector2(1,-1), new Vector2(1,1) }) {
                    var point = layer.center + Rotate(Vector2.Scale(corner, layer.size) / 2, layer.angle);
                    min = Vector2.Min(min, point); max = Vector2.Max(max, point);
                }
            var scale = XdImporter.Settings.SpriteSaveScale;
            min = new Vector2(Mathf.Floor(min.x * scale), Mathf.Floor(min.y * scale)) / scale;
            max = new Vector2(Mathf.Ceil(max.x * scale), Mathf.Ceil(max.y * scale)) / scale;
            var bounds = new Rect(min, max - min);
            var settings = XdImporter.Settings;
            var description = "XD-merged-shadow-v1\n" + bounds.ToString("R") + "\n" + scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var layer in layers) description += "\n" + holder.GetSourceFingerprint(layer.image.Sprite) + ":" +
                layer.center.ToString("R") + ":" + layer.size.ToString("R") + ":" + layer.angle.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" + (layer.image.Color ?? Color.white).ToString("R");
            var hash = FastHash.CalculateHash(description);
            var name = "MergedShadow_" + hash.ToString("x8") + ".png";
            holder.Save(name, () => Rasterize(layers, holder, bounds, scale, settings), description);
            var originalNames = new HashSet<string>(layers.Select(x => x.image.Sprite));
            // Keep assets shared by another generated node; discard only the now-consumed layers.
            assets.RemoveAll(x => originalNames.Contains(x.FileName) && !elements.OfType<ObjectElement>()
                .Any(e => e.Components.OfType<ImageComponent>().Any(image => image.Sprite == x.FileName)));
            assets.Add(new SpriteAsset(name, hash, bounds.size, null, new Border(0, 0, 0, 0)));
            var index = components.IndexOf(foreground);
            components[index] = new ImageComponent(name, Color.white, Vector2Int.one, hash, foreground.RaycastTarget);
            return (position + Rotate(bounds.center, rotation), bounds.size);
        }
        private static Color Sample(Color32[] pixels, int width, int height, float u, float v) {
            var fx = u * width - .5f; var fy = v * height - .5f;
            var x = Mathf.FloorToInt(fx); var y = Mathf.FloorToInt(fy);
            var x0 = Mathf.Clamp(x, 0, width - 1); var x1 = Mathf.Clamp(x + 1, 0, width - 1);
            var y0 = Mathf.Clamp(y, 0, height - 1); var y1 = Mathf.Clamp(y + 1, 0, height - 1);
            return Color.LerpUnclamped(Color.LerpUnclamped(pixels[y0 * width + x0], pixels[y0 * width + x1], fx - x),
                Color.LerpUnclamped(pixels[y1 * width + x0], pixels[y1 * width + x1], fx - x), fy - y);
        }
        private static byte[] Rasterize(List<Layer> layers, XdAssetHolder holder, Rect bounds, float scale, XdImportSettings settings) {
            var width = Mathf.Max(1, Mathf.RoundToInt(bounds.width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(bounds.height * scale));
            var pixels = new Color[checked(width * height)];
            foreach (var layer in layers) {
                var bytes = holder.Load(layer.image.Sprite);
                if (layer.vector) bytes = SvgToPng.Convert(Encoding.UTF8.GetString(bytes), layer.size, ViewportOptions.PreserveViewport, settings);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try {
                    if (!texture.LoadImage(bytes)) throw new InvalidOperationException("Cannot decode merged shadow layer.");
                    texture.wrapMode = TextureWrapMode.Clamp;
                    var tint = layer.image.Color ?? Color.white;
                    var source = texture.GetPixels32();
                    var cosine = Mathf.Cos(-layer.angle * Mathf.Deg2Rad); var sine = Mathf.Sin(-layer.angle * Mathf.Deg2Rad);
                    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) {
                        var point = new Vector2(bounds.xMin + (x + .5f) / scale, bounds.yMax - (y + .5f) / scale);
                        var delta = point - layer.center;
                        var u = (delta.x * cosine - delta.y * sine) / layer.size.x + .5f;
                        var v = .5f - (delta.x * sine + delta.y * cosine) / layer.size.y;
                        if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                        var color = Sample(source, texture.width, texture.height, u, v) * tint;
                        var destination = pixels[y * width + x];
                        pixels[y * width + x] = new Color(color.r * color.a + destination.r * (1 - color.a),
                            color.g * color.a + destination.g * (1 - color.a), color.b * color.a + destination.b * (1 - color.a),
                            color.a + destination.a * (1 - color.a));
                    }
                } finally { UnityEngine.Object.DestroyImmediate(texture); }
            }
            for (var i = 0; i < pixels.Length; i++) {
                var color = pixels[i];
                if (color.a > 0) pixels[i] = new Color(color.r / color.a, color.g / color.a, color.b / color.a, color.a);
            }
            var output = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try { output.SetPixels(pixels); output.Apply(); return output.EncodeToPNG(); }
            finally { UnityEngine.Object.DestroyImmediate(output); }
        }
    }
}
