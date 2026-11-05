using UnityEngine;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd.CommonTrigger
{
    [CreateAssetMenu(menuName = "UniXD/XD/Triggers/XdCommentOut", fileName = nameof(XdCommentOutTrigger))]
    public class XdCommentOutTrigger : XdImportTrigger
    {
        [SerializeField] private string startsWith = "#";

        public override XdObjectJson OnCreateXdObject(XdObjectJson xdObject)
        {
            var xdObjectName = xdObject?.Name ?? "";
            if (xdObjectName.StartsWith(startsWith)) return null;

            return xdObject;
        }
    }
}