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
Only the favicon SVG response permits this stylesheet through its computed CSP hash; other
non-OAuth responses retain their existing policy.

The assembly embeds the images. A closed path map serves GET and HEAD before auth, version
enforcement and rate limiting. Security headers still apply. Stable discovery URLs use a
seven-day public cache without `immutable`, allowing subsequent brand updates to expire.

`google-mark.svg` copies the four paths, fills, 48px view box and 20px display size from
`packages/shared/src/contracts/brand/GoogleMark.ts`, which the web sign-in imports.
The sign-in image is decorative; its existing link label supplies the accessible name.
The button uses the web sign-in's 12px horizontal padding and 10px gap with existing tokens.
See [Google's branding guidelines](https://developers.google.com/identity/branding-guidelines).
OAuth's existing `default-src 'self'` already permits these same-origin images.

## BIMI icon

`Assets/orbit-bimi.svg` is embedded at `/bimi/orbit.svg` with `image/svg+xml`.
After the API release, its public URL is `https://api.useorbit.org/bimi/orbit.svg`.
GET returns the committed bytes; HEAD returns the same headers without a body.

`Sources/orbit-mark.svg` is an unmodified copy of `design/brand/orbit-mark.svg`
from `thomasluizon/orbit-ui-mobile` at `a59bbcc43a67ad19aa7b25f084be25d01111fc0e`.
The platform composition follows that repository's `tools/generate-brand-assets.mjs`
and `design/brand/exports/play-icon-512.png`: a `#F4F4F6` body, a `#C4530F` moon,
and a square `#09090B` background, with the ink centred at 60% of the canvas width.
The vector generator solves cubic curve extrema instead of trimming raster pixels;
the original path data and even-odd fill rule remain intact. Each path carries its
own transform, avoiding grouped objects and stylesheets.

Rebuild with Node.js, without third-party packages:

```sh
node tools/generate-bimi-logo.mjs
node tools/generate-bimi-logo.mjs --check
```

The generator supports the canonical mark's absolute M, L, C and Z commands and
rejects other commands. When refreshing the mark, copy the canonical source,
regenerate, and review the geometry and composition assertions in `BimiLogoTests`.
Run `node --test tools/__tests__/generate-bimi-logo.test.mjs` to check reproducibility,
the SVG Tiny PS profile, and drift detection without changing the committed asset.

The [Google SVG requirements](https://knowledge.workspace.google.com/admin/security/create-a-bimi-svg-file-detailed-steps)
and [BIMI Group profile guidance](https://bimigroup.org/creating-bimi-svg-logo-files/)
require SVG Tiny PS 1.2, a title, no root x/y, and no scripts, animation or external
references. This icon also has an accessibility description, absolute dimensions
above 96 pixels, a square view box, a solid background, and no bitmaps, text shapes
or grouped objects. It is below the 32 KB limit.

For full schema validation, download the BIMI Group's
[SVG PS schema](https://bimigroup.org/resources/SVG_PS-latest.rnc.txt) and follow its
[RNC validation instructions](https://bimigroup.org/using-the-rnc-schema-to-validate-bimi-svg-images/):

```sh
pyjing -c /path/to/SVG_PS-latest.rnc.txt src/Orbit.Api/Branding/Assets/orbit-bimi.svg
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~BimiLogoTests|FullyQualifiedName~BrandDiscoveryTests'
```

The unit tests check the committed profile, canonical geometry, centred composition,
embedded bytes, and anonymous GET/HEAD through the production middleware pipeline
without starting a server. Publishing this asset prepares the logo for a certificate;
DNS, certificate issuance, sender changes and Gmail verification belong to the
owner's follow-up path.
