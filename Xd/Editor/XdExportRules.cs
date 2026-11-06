using System;
using System.Collections.Generic;
using System.Linq;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public static class XdExportRules
    {
        internal static XdImportTrigger[] ActiveTriggers;

        private sealed class FinalLayerNames : Dictionary<string, string>
        {
            internal readonly HashSet<string> resolvedIds = new HashSet<string>();
            internal readonly Dictionary<string, string> paths = new Dictionary<string, string>();

            internal FinalLayerNames(IDictionary<string, string> names) : base(names) { }
        }

        internal static bool TryGetFinalLayerName(IReadOnlyDictionary<string, string> names, string id, out string name)
        {
            name = null;
            return names is FinalLayerNames final && final.resolvedIds.Contains(id) && final.TryGetValue(id, out name);
        }
        public static bool IsFlattenExcluded(XdObjectJson node) => XdImporter.FlattenExcludedIds.Contains(SvgGroupParser.GroupId(node));

        internal static string GetFinalLayerPath(IReadOnlyDictionary<string, string> names, string id) =>
            names is FinalLayerNames final && final.paths.TryGetValue(id, out var path) ? path : "";

        internal static XdObjectJson ResolvePreviewNode(XdObjectJson node, IReadOnlyDictionary<string, XdObjectJson> symbols) =>
            symbols == null ? node : ResolveNamingNodes(new[] { node }, symbols, new HashSet<string>())[0];

        // Copy only changed groups so toggling a preview omission never mutates XD source data.
        public static XdObjectJson[] FilterPreviewNodes(IEnumerable<XdObjectJson> roots, ISet<string> omitted)
        {
            XdObjectJson Filter(XdObjectJson node)
            {
                if (omitted.Contains(SvgGroupParser.GroupId(node))) return null;
                if (node.Group == null) return node;
                var original = node.Group.Children ?? Array.Empty<XdObjectJson>();
                var children = original.Select(Filter).Where(x => x != null).ToArray();
                if (original.Length > 0 && children.Length == 0) return null;
                if (children.SequenceEqual(original)) return node;
                var copy = new XdObjectJson();
                foreach (var property in typeof(XdObjectJson).GetProperties().Where(x => x.CanRead && x.CanWrite))
                    property.SetValue(copy, property.GetValue(node));
                copy.Group = new XdObjectGroupJson { Children = children };
                return copy;
            }
            return roots.Select(Filter).Where(x => x != null).ToArray();
        }


        public static string NormalizeName(string original, string manual = null, XdImportSettings settings = null)
        {
            var name = string.IsNullOrWhiteSpace(manual) ? original ?? "" : manual.Trim();
            if (!string.IsNullOrWhiteSpace(manual) && (name.Any(c => char.IsControl(c) || "/\\:*?\"<>|".IndexOf(c) >= 0) || name == "." || name == ".."))
                throw new ImportException("覆盖名含非法文件名字符：" + name);
            return ApplyNameTriggers(name, settings);
        }

        private static string ApplyNameTriggers(string name, XdImportSettings settings = null)
        {
            foreach (var trigger in ActiveTriggers ?? settings?.XdTriggers ?? XdImporter.Settings?.XdTriggers ?? Array.Empty<XdImportTrigger>())
                name = trigger.OnExportName(name);
            return name;
        }

        public static Dictionary<string, string> BuildLayerNames(IEnumerable<XdObjectJson> roots, XdImportSettings settings,
            IReadOnlyDictionary<string, string> manual, IReadOnlyDictionary<string, XdObjectJson> symbols = null)
        {
            return BuildLayerNames(roots, settings, manual, symbols, XdImporter.ScrollViewModes);
        }

        internal static Dictionary<string, string> BuildLayerNames(IEnumerable<XdObjectJson> roots, XdImportSettings settings,
            IReadOnlyDictionary<string, string> manual, IReadOnlyDictionary<string, XdObjectJson> symbols,
            IReadOnlyDictionary<string, XdScrollViewMode> scrollViewModes)
        {
            var names = manual.ToDictionary(x => x.Key, x => x.Value);
            var previous = ActiveTriggers;
            try
            {
                ActiveTriggers = ActiveTriggers ?? settings?.XdTriggers ?? Array.Empty<XdImportTrigger>();
                var nodes = symbols == null ? roots.ToArray() : ResolveNamingNodes(roots, symbols, new HashSet<string>());
                foreach (var trigger in ActiveTriggers) trigger.OnBuildLayerNames(nodes, names, symbols);
                return BuildUniqueSiblingNames(nodes, names, settings, scrollViewModes);
            }
            finally { ActiveTriggers = previous; }
        }

        private static Dictionary<string, string> BuildUniqueSiblingNames(XdObjectJson[] roots, Dictionary<string, string> requestedNames, XdImportSettings settings,
            IReadOnlyDictionary<string, XdScrollViewMode> scrollViewModes)
        {
            var final = new FinalLayerNames(requestedNames);
            void VisitSiblings(XdObjectJson[] siblings, string parentPath = "")
            {
                siblings = siblings.Where(node => node != null).ToArray();
                var originalNames = siblings.Select(node =>
                {
                    requestedNames.TryGetValue(SvgGroupParser.GroupId(node) ?? "", out var manual);
                    return NormalizeName(node.GetOriginalSimpleName(), manual, settings);
                }).ToArray();
                var reserved = new HashSet<string>(originalNames, StringComparer.OrdinalIgnoreCase);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < siblings.Length; i++)
                {
                    var name = originalNames[i];
                    if (!used.Add(name))
                    {
                        counters.TryGetValue(name, out var number);
                        string candidate;
                        do { candidate = name + "_" + ++number; } while (reserved.Contains(candidate) || !used.Add(candidate));
                        counters[name] = number;
                        name = candidate;
                    }
                    var id = SvgGroupParser.GroupId(siblings[i]);
                    if (!string.IsNullOrEmpty(id))
                    {
                        final[id] = name;
                        final.resolvedIds.Add(id);
                        final.paths[id] = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
                    }
                }
                foreach (var node in siblings)
                {
                    var id = SvgGroupParser.GroupId(node);
                    var path = id != null && final.paths.TryGetValue(id, out var resolvedPath) ? resolvedPath : parentPath;
                    VisitSiblings(GetNamingChildren(node, scrollViewModes), path);
                    VisitSiblings(node.Meta?.Ux?.ClipPathResources?.Children ?? Array.Empty<XdObjectJson>(), path);
                }
            }
            VisitSiblings(roots);
            return final;
        }

        private static XdObjectJson[] GetNamingChildren(XdObjectJson node, IReadOnlyDictionary<string, XdScrollViewMode> scrollViewModes)
        {
            var children = node.Group?.Children ?? Array.Empty<XdObjectJson>();
            if (node.Type != "group") return children;

            // RepeatGridGroupParser runs before ScrollGroupParser and retains just
            // the prototype, promoting it past its repeat-grid row wrapper.
            if (RepeatGridGroupParser.Is(node))
            {
                var prototype = GetRepeatGridPrototype(node);
                return prototype == null ? children : new[] { prototype };
            }

            var scrollingType = node.Meta?.Ux?.ScrollingType;
            if (string.IsNullOrWhiteSpace(scrollingType) || scrollViewModes == null ||
                !scrollViewModes.TryGetValue(SvgGroupParser.GroupId(node) ?? "", out var mode) || mode == XdScrollViewMode.None)
                return children;

            var repeatGrid = children.FirstOrDefault(child => child != null && RepeatGridGroupParser.Is(child));
            var spacer = children.FirstOrDefault(child => child != null && child.NameEndsWith("spacer"));
            var retained = spacer == null ? children : children.Where(child => child != spacer).ToArray();
            var item = GetRepeatGridPrototype(repeatGrid);
            if (item == null) return retained;

            var promoted = new[] { item };
            if (node.HasParameter("multiitems") && item.Group?.Children != null)
                promoted = item.Group.Children.Select(child => RepeatGridGroupParser.Is(child, scrollingType)
                    ? GetRepeatGridPrototype(child) ?? child : child).ToArray();
            return retained.Where(child => child != repeatGrid).Concat(promoted).ToArray();
        }

        private static XdObjectJson GetRepeatGridPrototype(XdObjectJson node)
        {
            return node?.Group?.Children?.FirstOrDefault()?.Group?.Children?.FirstOrDefault();
        }

        // The renderer resolves instances before naming. Preview uses the same
        // effective master name and inherited fields, while retaining instance IDs.
        private static XdObjectJson[] ResolveNamingNodes(IEnumerable<XdObjectJson> nodes, IReadOnlyDictionary<string, XdObjectJson> symbols, HashSet<string> active)
        {
            XdObjectJson Resolve(XdObjectJson node)
            {
                var reference = node.Type == "syncRef" ? node.SyncSourceGuid : null;
                var effective = node;
                var entered = false;
                if (reference != null && symbols.TryGetValue(reference, out var source))
                {
                    if (!active.Add(reference)) return ShallowCopy(node);
                    entered = true;
                    effective = (XdObjectJson)MergeNamingElement(typeof(XdObjectJson), node, source);
                    effective.Id = node.Id ?? node.Guid ?? source.Id;
                    effective.Name = string.IsNullOrEmpty(node.Name) ? source.Name : node.Name;
                    effective.Type = source.Type;
                    effective.Shape = source.Shape;
                }
                try
                {
                    var copy = ShallowCopy(effective);
                    if (effective.Group != null)
                    {
                        copy.Group = ShallowCopy(effective.Group);
                        copy.Group.Children = ResolveNamingNodes(effective.Group.Children ?? Array.Empty<XdObjectJson>(), symbols, active);
                    }
                    var clip = effective.Meta?.Ux?.ClipPathResources;
                    if (clip != null)
                    {
                        copy.Meta = ShallowCopy(effective.Meta);
                        copy.Meta.Ux = ShallowCopy(effective.Meta.Ux);
                        copy.Meta.Ux.ClipPathResources = ShallowCopy(clip);
                        copy.Meta.Ux.ClipPathResources.Children = ResolveNamingNodes(clip.Children ?? Array.Empty<XdObjectJson>(), symbols, active);
                    }
                    return copy;
                }
                finally { if (entered) active.Remove(reference); }
            }
            return nodes.Where(x => x != null).Select(Resolve).ToArray();
        }

        private static IXdJsonElement MergeNamingElement(Type type, IXdJsonElement instance, IXdJsonElement source)
        {
            if (instance == null || source == null) return source;
            var copy = (IXdJsonElement)Activator.CreateInstance(type);
            foreach (var property in type.GetProperties().Where(x => x.CanRead && x.CanWrite))
            {
                var value = property.GetValue(instance);
                var sourceValue = property.GetValue(source);
                property.SetValue(copy, typeof(IXdJsonElement).IsAssignableFrom(property.PropertyType)
                    ? MergeNamingElement(property.PropertyType, (IXdJsonElement)value, (IXdJsonElement)sourceValue)
                    : value ?? sourceValue);
            }
            return copy;
        }

        private static T ShallowCopy<T>(T source) where T : class, new()
        {
            var copy = new T();
            foreach (var property in typeof(T).GetProperties().Where(x => x.CanRead && x.CanWrite))
                property.SetValue(copy, property.GetValue(source));
            return copy;
        }

    }
}
