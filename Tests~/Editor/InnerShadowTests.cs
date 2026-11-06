using System;
using System.Linq;
using System.Reflection;
using BIEngine.UniXD.Xd;
using BIEngine.UniXD.Xd.Parsing.Internal;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.Tests {
    public sealed class InnerShadowTests {
        private static Type renderer => typeof(XdImporter).Assembly.GetType("BIEngine.UniXD.Xd.XdInnerShadowRenderer");
        [TestCase(0f, 6f)] [TestCase(5f, 0f)]
        public void InnerShadowPreservesMaskAndBounds(float dx, float radius) {
            var source = new Texture2D(48, 30, TextureFormat.RGBA32, false);
            var result = new Texture2D(2, 2);
            try {
                var pixels = new Color32[48 * 30];
                for (var y=2;y<28;y++) for(var x=2;x<46;x++) pixels[y*48+x]=new Color32(255,180,80,255);
                source.SetPixels32(pixels);source.Apply();
                var shadow = new XdDropShadowJson {Dx=dx,Dy=0,Radius=radius,Color=new XdColorJson {Mode="RGB",Alpha=.8f,Value=new XdColorValueJson {R=0,G=0,B=0}}};
                var bytes=(byte[])renderer.GetMethod("Apply",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,
                    new object[] {source.EncodeToPNG(),new Vector2(48,30),new[] {shadow}});
                Assert.That(result.LoadImage(bytes),Is.True);
                Assert.That(result.width,Is.EqualTo(48));Assert.That(result.height,Is.EqualTo(30));
                var actual=result.GetPixels32();
                Assert.That(actual.Select(x=>x.a),Is.EqualTo(pixels.Select(x=>x.a)),"No paint outside the original alpha, no alpha growth");
                Assert.That(actual[15*48+2].r,Is.LessThan(actual[15*48+24].r),"Inward shadow changes edge, not the whole fill uniformly");
            } finally {Object.DestroyImmediate(source);Object.DestroyImmediate(result);}
        }
        [Test] public void ReadsActualXdInnerShadowFilterAndHonorsVisibility() {
            var node=Utf8Json.JsonSerializer.Deserialize<XdObjectJson>("{\"type\":\"shape\",\"style\":{\"filters\":[{\"type\":\"uxdesign#innerShadow\",\"params\":{\"innerShadows\":[{\"dx\":0,\"dy\":0,\"r\":6,\"color\":{\"mode\":\"RGB\",\"value\":{\"r\":255,\"g\":255,\"b\":112},\"alpha\":0.8}}]}}]}}");
            var method=renderer.GetMethod("HasOwnShadow",BindingFlags.NonPublic|BindingFlags.Static);
            Assert.That((bool)method.Invoke(null,new object[] {node}),Is.True);
            node.Style.Filters[0].Visible=false;
            Assert.That((bool)method.Invoke(null,new object[] {node}),Is.False);
        }
    }
}
