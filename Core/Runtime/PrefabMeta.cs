using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "AkyuiUnity", "AkyuiUnity", "AkyuiMeta")]
    public class PrefabMeta : MonoBehaviour
    {
        [SerializeField] public uint hash;
        [SerializeField] public GameObject root;
        [SerializeField] public Object[] assets;
        [SerializeField] public MetaUserData[] userData;
        [SerializeField] public EidToObject[] eidToObjects;

        public MetaUserData FindUserData(string key)
        {
            return userData?.FirstOrDefault(x => x.key == key);
        }
    }

    [Serializable]
    public class MetaUserData
    {
        [SerializeField] public string key;
        [SerializeField] public string value;
    }

    [Serializable]
    public class EidToObject
    {
        [SerializeField] public uint eid;
        [SerializeField] public GameObject gameObject;
    }
}
