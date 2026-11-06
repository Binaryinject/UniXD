# Changelog

## Unreleased

- Fix tolerant nine-slicing incorrectly treating long RGB/alpha gradients as repeated stripes; bound the entire candidate run.
- Preserve original encoded bytes when slicing makes no changes; avoid unnecessary PNG/JPEG encoding and output texture allocation.
- Optimize managed exact comparisons, row copying, temporary mipmaps and PNG dimension reads; invalidate legacy processed-cache results.
- Add optional Burst 1.8+ / Jobs acceleration for large exact-mode textures, with a managed fallback and no mandatory Burst dependency.
- Add 12 portable slicing regressions and measured performance documentation.

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


## 2026-11-06
- Added per-merge outer-shadow output selection with separate layers as the backward-compatible default; combined PNG bounds include blur, offsets and rotation.
- Clarified single-SVG group export in the flatten window.
- Added clipped inner shadows for shape/image fills and flattened groups, including original-alpha and geometry preservation, bounded pixel caching and regression coverage.

- Protect shared and copied sprite references before removing unused export resources, using a batched reverse-dependency index.
- Cache immutable artboard options with XD content-hash invalidation.
- Preserve referenced-layer source names and per-instance rename IDs; reject empty or leaf merge selections.
- Compact artboard and layer settings into single-line rows, with hierarchy guides, component icons and original/reverse/renamed-first artboard sorting.
- Collapse omitted subtrees when opening settings and immediately after marking them omitted; allow manual expansion afterward.
- Add portable reference-safety, reference-name, merge-eligibility, sorting and shadow regression tests.

- Bundle optional OSA export adapters and PrimeTween button feedback; preserve trigger asset GUIDs and prefer existing project components. Detect assembly definitions and compile optional backends with support symbols without mandatory third-party dependencies.
- Scroll overflowing layer names as a clipped single-line marquee, with cached widths and visible-row repaint throttling.

- Export XD pasteboard loose layers without requiring artboard metadata; derive bounds from visible content and preserve root IDs. Include pasteboard in the default primary sprite library names.
