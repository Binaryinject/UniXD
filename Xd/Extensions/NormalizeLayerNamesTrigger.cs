using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd.Extensions
{
    [CreateAssetMenu(menuName = "UniXD/XD/Triggers/NormalizeLayerNamesTrigger", fileName = nameof(NormalizeLayerNamesTrigger))]
    public class NormalizeLayerNamesTrigger : XdImportTrigger
    {
        private static readonly Regex ContainsChinese = new Regex("[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]");

        public override string OnExportName(string name) => Regex.Replace(name, @"\s+", "_");

        public override void OnBuildLayerNames(XdObjectJson[] roots, IDictionary<string, string> names,
            IReadOnlyDictionary<string, XdObjectJson> symbols)
        {
            var nodes = new List<KeyValuePair<string, XdObjectJson>>();
            var active = new HashSet<string>();
            void Collect(XdObjectJson node)
            {
                var source = node;
                var reference = node.Type == "syncRef" ? node.SyncSourceGuid : null;
                if (reference != null && symbols != null)
                {
                    if (!symbols.TryGetValue(reference, out source) || !active.Add(reference)) return;
                }
                try
                {
                    nodes.Add(new KeyValuePair<string, XdObjectJson>(SvgGroupParser.GroupId(node), source));
                    foreach (var child in source.Group?.Children ?? Array.Empty<XdObjectJson>()) Collect(child);
                    foreach (var child in source.Meta?.Ux?.ClipPathResources?.Children ?? Array.Empty<XdObjectJson>()) Collect(child);
                }
                finally { if (reference != null) active.Remove(reference); }
            }
            foreach (var root in roots) Collect(root);

            bool NeedsNumber(XdObjectJson node) => ContainsChinese.IsMatch(node.GetOriginalSimpleName());
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in nodes)
            {
                names.TryGetValue(pair.Key ?? "", out var manual);
                if (!string.IsNullOrWhiteSpace(manual) || !NeedsNumber(pair.Value))
                    used.Add(XdExportRules.NormalizeName(string.IsNullOrWhiteSpace(manual) ? pair.Value.GetOriginalSimpleName() : manual.Trim()));
            }
            var counters = new Dictionary<string, int>();
            foreach (var pair in nodes)
            {
                if (string.IsNullOrEmpty(pair.Key) || !NeedsNumber(pair.Value) ||
                    (names.TryGetValue(pair.Key, out var manual) && !string.IsNullOrWhiteSpace(manual))) continue;
                var prefix = Prefix(pair.Value);
                counters.TryGetValue(prefix, out var number);
                string generated;
                do { generated = prefix + "_" + ++number; } while (!used.Add(generated));
                counters[prefix] = number;
                names[pair.Key] = generated;
            }
        }

        private static string Prefix(XdObjectJson node)
        {
            if (MaskGroupParser.Is(node) || node.Type == "clipPath") return "Mask";
            if (node.Type == "text") return "Text";
            if (node.Type == "shape")
            {
                if (node.Style?.Fill?.Type == "pattern" || node.Style?.Fill?.Pattern != null) return "Texture";
                if (node.Shape != null && SvgUtil.Types.Contains(node.Shape.Type)) return "Vector";
            }
            if (node.Type == "group" || node.Group != null) return "Folder";
            return "Other";
        }
    }
}
