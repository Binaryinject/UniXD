# Changelog

## 1.0.0 — 2026-11-05

- Consolidate AkyuiUnity, AkyuiUnity.Xd and Auto9Slicer into `com.biengine.unixd`.
- Rename maintained namespaces and assemblies to `BIEngine.UniXD.*`, retaining original script and assembly GUIDs.
- Remove redundant type prefixes: `ILoader`, `ImportTrigger`, `ImportSettings`, `XdLoader`, `PrefabGenerator` and `PrefabMeta`; retain serialized-data migration without public legacy aliases.
- Add configurable artboard/layer export workflow, component rules, naming overrides and reviewed JSON recipes.
- Support optional SVGImage output, correct Canvas UV2, rectangle gradient coordinates and gradient precision.
- Export padded soft-shadow decoration behind foreground graphics without resizing buttons or changing text.
- Add optional Linear transparency matching with a configurable reference background.
- Share sprites/materials across artboards, use readable material names and filter atlas inputs by area/type.
- Cache import work and virtualize large layer lists; support Shift range toggles in artboard and layer windows.

This is a new maintenance line. See README and Documentation~ for upstream baselines and compatibility limits.
