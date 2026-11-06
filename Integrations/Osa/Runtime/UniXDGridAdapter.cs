#if UNIXD_OSA_SUPPORT
namespace BIEngine.UniXD.Osa {
using Com.TheFallenGames.OSA.CustomAdapters.GridView;
using System;

public class UniXDGridAdapter : GridAdapter<GridParams, UniXDCellViewsHolder>
{
	public Action<UniXDCellViewsHolder> updateCellViewsHolder;
#if UNITY_EDITOR
    public void ConfigureForExport(GridParams parameters) {
        _Params = parameters;
    }
#endif
	#region OSA implementation

	protected override void UpdateCellViewsHolder(UniXDCellViewsHolder viewsHolder) {
		viewsHolder.root.name = viewsHolder.ItemIndex.ToString();
		updateCellViewsHolder?.Invoke(viewsHolder);
	}

	#endregion
}

public class UniXDCellViewsHolder : CellViewsHolder {
}

}
#endif
