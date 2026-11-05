using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing.Internal;
namespace BIEngine.UniXD.Xd.Extensions
{
    public enum XdAutoComponentType { Button, Toggle, InputField }
    public enum XdNameMatch { Prefix, Suffix, Exact, Contains }
    public enum XdNameSource { Original, Exported, Either }

    [Serializable]
    public sealed class XdAutoComponentRule
    {
        public bool enabled = true;
        public XdAutoComponentType component;
        public XdNameMatch match;
        public string pattern;
        public XdNameSource nameSource = XdNameSource.Either;
        public bool ignoreCase = true;

        internal bool Matches(string original, string exported)
        {
            if (!enabled || string.IsNullOrWhiteSpace(pattern)) return false;
            return (nameSource != XdNameSource.Exported && MatchesName(original))
                || (nameSource != XdNameSource.Original && MatchesName(exported));
        }

        private bool MatchesName(string name)
        {
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            name = name ?? "";
            switch (match)
            {
                case XdNameMatch.Prefix: return name.StartsWith(pattern, comparison);
                case XdNameMatch.Suffix: return name.EndsWith(pattern, comparison);
                case XdNameMatch.Exact: return string.Equals(name, pattern, comparison);
                default: return name.IndexOf(pattern, comparison) >= 0;
            }
        }
    }

    [CreateAssetMenu(menuName = "UniXD/XD/Triggers/AutoComponentRules", fileName = nameof(AutoComponentRulesTrigger))]
    public sealed class AutoComponentRulesTrigger : XdImportTrigger
    {
        [SerializeField, Tooltip("首个匹配的交互组件生效；同时适用于合图节点。")]
        private XdAutoComponentRule[] m_Rules = CreateDefaults();
        public override string settingsTitle => "自动组件命名规则";
        public override bool ShouldUseGroupParser(IXdGroupParser parser, XdObjectJson node, XdObjectJson[] parents)
            => !(parser is ButtonGroupParser || parser is ToggleGroupParser || parser is InputFieldGroupParser);
        public static XdAutoComponentRule[] CreateDefaults() => new[]
        {
            new XdAutoComponentRule { component = XdAutoComponentType.Button, match = XdNameMatch.Prefix, pattern = "Btn_" },
            new XdAutoComponentRule { component = XdAutoComponentType.Button, match = XdNameMatch.Suffix, pattern = "button" },
            new XdAutoComponentRule { component = XdAutoComponentType.Toggle, match = XdNameMatch.Suffix, pattern = "toggle" },
            new XdAutoComponentRule { component = XdAutoComponentType.InputField, match = XdNameMatch.Suffix, pattern = "inputfield" },
        };

        public override IComponent[] OnBuildComponents(XdObjectJson node, IComponent[] components, string exported)
        {
            var result = components.ToList();
            // Selectable components cannot coexist on one object. Explicit parser output wins.
            if (result.Any(IsSelectable)) return result.ToArray();
            foreach (var rule in m_Rules ?? Array.Empty<XdAutoComponentRule>())
            {
                if (rule == null || !rule.Matches(node.GetOriginalSimpleName(), exported)) continue;
                switch (rule.component)
                {
                    case XdAutoComponentType.Button: result.Add(new ButtonComponent()); break;
                    case XdAutoComponentType.Toggle: result.Add(new ToggleComponent()); break;
                    case XdAutoComponentType.InputField: result.Add(new InputFieldComponent()); break;
                }
                break;
            }
            return result.ToArray();
        }

        private static bool IsSelectable(IComponent component) => component is ButtonComponent || component is ToggleComponent
            || component is InputFieldComponent || component is VerticalScrollbarComponent || component is HorizontalScrollbarComponent;

    }
}
