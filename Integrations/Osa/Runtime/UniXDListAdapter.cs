#if UNIXD_OSA_SUPPORT
namespace BIEngine.UniXD.Osa {
using Com.TheFallenGames.OSA.Core;
using System;
using UnityEngine;

public class UniXDListAdapter : OSA<BaseParams, UniXDItemViewsHolder> {
    [SerializeField, Tooltip("未提供 createViewsHolder 时使用的导出条目模板。")]
    private RectTransform m_ItemPrefab;

    public RectTransform itemPrefab => m_ItemPrefab;

#if UNITY_EDITOR
    public void ConfigureForExport(BaseParams parameters, RectTransform template) {
        _Params = parameters;
        m_ItemPrefab = template;
    }
#endif

    public delegate GameObject CreateViewsHolderDelegate(int itemIndex);

    public delegate float CollectItemsSizesDelegate(int itemIndex);

    public CreateViewsHolderDelegate createViewsHolder = null;
    public CollectItemsSizesDelegate collectItemsSizes = null;
    public Action<UniXDItemViewsHolder> updateViewsHolder;
    public Action<UniXDItemViewsHolder> onRootCreated;
    public Action<(UniXDItemViewsHolder, int)> onBeforeRecycleOrDisableViewsHolder;



    #region OSA implementation

    protected override UniXDItemViewsHolder CreateViewsHolder(int itemIndex) {
        var prefab = createViewsHolder != null ? createViewsHolder(itemIndex) : m_ItemPrefab != null ? m_ItemPrefab.gameObject : null;
        if (prefab == null)
            throw new System.InvalidOperationException($"{nameof(UniXDListAdapter)} '{name}' requires itemPrefab or createViewsHolder before adding items.");
        var instance = new UniXDItemViewsHolder();
        instance.onRootCreated = onRootCreated;
        instance.Init(prefab, _Params.Content, itemIndex);
        return instance;
    }

    protected override void UpdateViewsHolder(UniXDItemViewsHolder newOrRecycled) {
        updateViewsHolder?.Invoke(newOrRecycled);

    }

    protected override void CollectItemsSizes(ItemCountChangeMode changeMode, int count, int indexIfInsertingOrRemoving, ItemsDescriptor itemsDesc) {
        base.CollectItemsSizes(changeMode, count, indexIfInsertingOrRemoving, itemsDesc);

        if (collectItemsSizes == null || changeMode == ItemCountChangeMode.REMOVE || count == 0)
            return;

        var indexOfFirstItemThatWillChangeSize = changeMode == ItemCountChangeMode.RESET ? 0 : indexIfInsertingOrRemoving;

        int end = indexOfFirstItemThatWillChangeSize + count;

        itemsDesc.BeginChangingItemsSizes(indexOfFirstItemThatWillChangeSize);
        for (int i = indexOfFirstItemThatWillChangeSize; i < end; ++i) {
            itemsDesc[i] = collectItemsSizes(i);
        }

        itemsDesc.EndChangingItemsSizes();
    }

    protected override void OnBeforeRecycleOrDisableViewsHolder(UniXDItemViewsHolder inRecycleBinOrVisible, int newItemIndex) {
        onBeforeRecycleOrDisableViewsHolder?.Invoke((inRecycleBinOrVisible, newItemIndex));
        base.OnBeforeRecycleOrDisableViewsHolder(inRecycleBinOrVisible, newItemIndex);
    }

    #endregion
}


public class UniXDItemViewsHolder : BaseItemViewsHolder {
    public Action<UniXDItemViewsHolder> onRootCreated;

    protected override void OnRootCreated(int itemIndex, bool activateRootGameObject = true, bool callCollectViews = true) {
        base.OnRootCreated(itemIndex, activateRootGameObject, callCollectViews);
        onRootCreated?.Invoke(this);
    }
}

}
#endif
