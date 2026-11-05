using System.Collections.Generic;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public class XdExtensionsUtil
    {
        public static void DebugTreeOutput(XdObjectJson xdObject)
        {
            List<string> Tree(XdObjectJson o, string indent)
            {
                var tree = new List<string>();
                tree.Add($"{indent}- {o.Name}");
                foreach (var c in o.Group?.Children ?? new XdObjectJson[]{})
                {
                    tree.AddRange(Tree(c, indent + "  "));
                }

                return tree;
            }

            Debug.Log($"{string.Join("\n", Tree(xdObject, ""))}");
        }
    }
}