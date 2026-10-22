# API brand assets

The source checkout is `orbit-ui-mobile`. Copy its generated assets without re-encoding:

| API asset | Canonical source |
|---|---|
| `favicon.ico` | `apps/web/app/favicon.ico` |
| `apple-touch-icon.png` | `apps/web/app/apple-icon.png` |
| `orbit-icon-512.png` | `design/brand/png/orbit-platform-icon-512.png` |
| `favicon.svg` | `design/brand/orbit-mark-accent.svg` |

The SVG keeps the canonical paths and adds a standalone stylesheet after its description.
It supplies the existing foreground tokens (`#1A1A1D` in light mode, `#F4F4F6` in dark mode)
and the existing `--primary` token (`#C4530F`), since an image cannot inherit the page's CSS.

The assembly embeds the images. A closed path map serves GET and HEAD before auth, version
enforcement and rate limiting. Security headers still apply. Stable discovery URLs use a
seven-day public cache without `immutable`, allowing subsequent brand updates to expire.
