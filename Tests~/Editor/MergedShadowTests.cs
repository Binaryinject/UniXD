using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BIEngine.UniXD.Loader;
using BIEngine.UniXD.Xd;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.Tests {
    public sealed class MergedShadowTests {
        private static byte[] Solid(Color color, int width, int height) {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try { texture.SetPixels(Enumerable.Repeat(color, width * height).ToArray()); texture.Apply(); return texture.EncodeToPNG(); }
            finally { Object.DestroyImmediate(texture); }
        }
        [Test] public void MissingSettingDefaultsToSeparateAndArchiveRoundTrips() {
            var type = typeof(XdImporter).Assembly.GetType("BIEngine.UniXD.Xd.XdFlattenHistory");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var file = "MergedShadowHistoryFixture.xd"; var board = "fixture-" + Guid.NewGuid().ToString("N");
            var capture = type.GetMethod("Capture", flags); var restore = type.GetMethod("Restore", flags);
            var load = type.GetMethod("LoadMergedShadows", flags);
            var old = capture.Invoke(null, new object[] {file, board});
            try {
                restore.Invoke(null, new object[] {file, board, "{}"});
                Assert.That(((HashSet<string>)load.Invoke(null, new object[] {file, board})).Count, Is.Zero);
                type.GetMethod("SaveMergedShadows", flags).Invoke(null, new object[] {file, board, new[] {"layer-b", "layer-a"}});
                var archive = capture.Invoke(null, new object[] {file, board});
                restore.Invoke(null, new object[] {file, board, "{}"});
                restore.Invoke(null, new object[] {file, board, archive});
                Assert.That((HashSet<string>)load.Invoke(null, new object[] {file, board}), Is.EquivalentTo(new[] {"layer-a", "layer-b"}));
            } finally {restore.Invoke(null, new object[] {file, board, old});}
        }
        [TestCase(1f, 0f)] [TestCase(.75f, 0f)] [TestCase(2f, 30f)]
        public void MergeIncludesOffsetShadowAndPreservesForeground(float scale, float rotation) {
            var settings = ScriptableObject.CreateInstance<XdImportSettings>();
            var old = XdImporter.Settings;
            typeof(XdImporter).GetProperty("Settings").SetValue(null, settings);
            typeof(BIEngine.UniXD.Editor.ScriptableObject.ImportSettings).GetField("spriteSaveScale", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(settings, scale);
            var holder = new XdAssetHolder(null);
            holder.Save("front.png", () => Solid(Color.red, Mathf.RoundToInt(100 * scale), Mathf.RoundToInt(40 * scale)), "front");
            holder.Save("shadow.png", () => Solid(new Color(0,0,0,.5f), Mathf.RoundToInt(120 * scale), Mathf.RoundToInt(60 * scale)), "shadow");
            var angle = rotation * Mathf.Deg2Rad;
            var offset = new Vector2(10 * Mathf.Cos(angle) - 5 * Mathf.Sin(angle), 10 * Mathf.Sin(angle) + 5 * Mathf.Cos(angle));
            var shadows = new List<IElement> { new ObjectElement(2,"shadow",offset,new Vector2(120,60),AnchorXType.Center,AnchorYType.Middle,rotation,true,
                new IComponent[] { new ImageComponent("shadow.png",Color.white,Vector2Int.one,2,false) },Array.Empty<uint>()) };
            var components = new List<IComponent> { new ImageComponent("front.png",Color.white,Vector2Int.one,1),new ButtonComponent() };
            var assets = new List<IAsset> {new SpriteAsset("front.png",1,new Vector2(100,40),null,null),new SpriteAsset("shadow.png",2,new Vector2(120,60),null,null)};
            try {
                var type = typeof(XdImporter).Assembly.GetType("BIEngine.UniXD.Xd.XdMergedShadowRenderer");
                var result = ((Vector2 position,Vector2 size))type.GetMethod("Merge",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,
                    new object[] {Vector2.zero,new Vector2(100,40),rotation,components,shadows,holder,assets,new List<IElement>()});
                Assert.That(result.size.x, Is.InRange(120f,120f+2/scale));
                Assert.That(result.size.y, Is.InRange(60f,60f+2/scale));
                Assert.That(Vector2.Distance(result.position,offset),Is.LessThan(2/scale));
                Assert.That(components.OfType<ButtonComponent>().Count(),Is.EqualTo(1));
                Assert.That(assets.Count,Is.EqualTo(1));
                var bytes=holder.Load(assets[0].FileName);
                Assert.That(holder.Load(assets[0].FileName),Is.EqualTo(bytes));
                var texture=new Texture2D(2,2);
                try {
                    texture.LoadImage(bytes);
                    Assert.That(texture.width,Is.EqualTo(Mathf.RoundToInt(result.size.x*scale)));
                    Assert.That(texture.height,Is.EqualTo(Mathf.RoundToInt(result.size.y*scale)));
                    Assert.That(texture.GetPixels32().Any(x=>x.r==255 && x.a==255),Is.True,"Foreground must stay opaque red");
                    Assert.That(texture.GetPixels32().Any(x=>x.r==0 && x.a>120 && x.a<136),Is.True,"Offset shadow must survive beyond foreground");
                } finally {Object.DestroyImmediate(texture);}
            } finally {typeof(XdImporter).GetProperty("Settings").SetValue(null,old);Object.DestroyImmediate(settings);}
        }
    }
}
