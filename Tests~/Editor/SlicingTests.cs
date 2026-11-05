using System;
using System.Collections.Generic;
using System.Reflection;
using BIEngine.UniXD.Extensions;
using BIEngine.UniXD.Slicing;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.Tests
{
    public sealed class SlicingTests
    {
        private readonly List<Object> m_Objects = new List<Object>();

        private Texture2D Texture(int width, int height, Func<int, int, Color32> pixel, TextureFormat format = TextureFormat.RGBA32)
        {
            var texture = new Texture2D(width, height, format, false);
            m_Objects.Add(texture);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) pixels[y * width + x] = pixel(x, y);
            texture.SetPixels32(pixels);
            return texture;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var item in m_Objects) if (item != null) Object.DestroyImmediate(item);
            m_Objects.Clear();
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void GradientsCannotAccumulateAdjacentTolerance(bool vertical, bool alpha)
        {
            var texture = Texture(64, 64, (x, y) =>
            {
                var value = (byte)(64 + (vertical ? y : x));
                return alpha ? new Color32(100, 100, 100, value) : new Color32(value, value, value, 255);
            });
            var sliced = Slicer.Slice(texture, new SliceOptions { Tolerate = 2 });
            m_Objects.Add(sliced.Texture);
            Assert.That(vertical ? sliced.Texture.height : sliced.Texture.width, Is.EqualTo(64));
        }

        [Test]
        public void FlatPanelsStillProduceNineSlices()
        {
            var texture = Texture(128, 96, (x, y) => x < 8 || y < 8 || x >= 120 || y >= 88
                ? new Color32(20, 20, 20, 255) : new Color32(220, 220, 220, 255));
            Assert.That(Slicer.TrySlice(texture, SliceOptions.Default, out var sliced), Is.True);
            m_Objects.Add(sliced.Texture);
            Assert.That(sliced.Texture.width, Is.LessThan(texture.width));
            Assert.That(sliced.Texture.height, Is.LessThan(texture.height));
        }

        [Test]
        public void UnchangedImagesKeepOriginalEncodedBytes()
        {
            var texture = Texture(64, 64, (x, y) => new Color32((byte)x, (byte)y, 100, 255));
            Assert.That(Slicer.TrySlice(texture, SliceOptions.Default, out var unused), Is.False);
            Assert.That(unused, Is.Null);
            var original = texture.EncodeToPNG();
            var bytes = original;
            IAsset asset = new SpriteAsset("gradient.png", 1, new Vector2(64, 64), null, null);
            var data = new Dictionary<string, object>();
            var trigger = ScriptableObject.CreateInstance<SetupAuto9SlicerTrigger>();
            m_Objects.Add(trigger);
            typeof(SetupAuto9SlicerTrigger).GetField("options", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(trigger, SliceOptions.Default);
            trigger.OnPreprocessAsset(null, ref bytes, ref asset, ref data);
            Assert.That(bytes, Is.SameAs(original));
            var border = ((SpriteAsset)asset).Border;
            Assert.That(border.Left + border.Right + border.Top + border.Bottom, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SourceDimensionsMatchPngHeaderAndJpegFallback(bool jpeg)
        {
            var texture = Texture(17, 23, (x, y) => new Color32((byte)x, (byte)y, 80, 255));
            var bytes = jpeg ? texture.EncodeToJPG() : texture.EncodeToPNG();
            var method = typeof(BIEngine.UniXD.Editor.Importer).GetMethod("GetSourceImageSize", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That((Vector2Int)method.Invoke(null, new object[] { bytes }), Is.EqualTo(new Vector2Int(17, 23)));
        }

        [TestCase(TextureFormat.RGB24)]
        [TestCase(TextureFormat.RGBA32)]
        [TestCase(TextureFormat.ARGB32)]
        [TestCase(TextureFormat.BGRA32)]
        public void BurstMatchesManagedAndDoesNotMutateInput(TextureFormat format)
        {
            var property = typeof(Slicer).GetProperty("accelerator", BindingFlags.Static | BindingFlags.NonPublic);
            var handler = property.GetValue(null);
            if (handler == null) Assert.Ignore("Install and enable Burst 1.8+ to exercise the optional backend.");
            var texture = Texture(1024, 1024, (x, y) => new Color32((byte)(x * 17), 80, 150, (byte)(x % 13 == 0 ? 0 : 197)), format);
            var input = texture.GetPixels32();
            SlicedTexture managed;
            property.SetValue(null, null);
            try { managed = Slicer.Slice(texture, SliceOptions.Default); }
            finally { property.SetValue(null, handler); }
            m_Objects.Add(managed.Texture);
            var accelerated = Slicer.Slice(texture, SliceOptions.Default);
            m_Objects.Add(accelerated.Texture);
            Assert.That(accelerated.Border.ToVector4(), Is.EqualTo(managed.Border.ToVector4()));
            Assert.That(accelerated.Texture.width, Is.EqualTo(managed.Texture.width));
            Assert.That(accelerated.Texture.height, Is.EqualTo(managed.Texture.height));
            CollectionAssert.AreEqual(managed.Texture.GetPixels32(), accelerated.Texture.GetPixels32());
            CollectionAssert.AreEqual(input, texture.GetPixels32());
        }
    }
}
