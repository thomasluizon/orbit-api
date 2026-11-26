# API brand assets

The assembly embeds the images in `Assets/`. Every icon is the Orbit mascot. Copy a generated image from `orbit-ui-mobile` without re-encoding it.

The current copies come from `orbit-ui-mobile` commit `93ca89d2f610b2e600315bcd4f0c793b01e25086` on `main`. When the brand export changes, copy the new files and update this commit.

| API asset | Canonical copy |
|---|---|
| `favicon.ico` | `apps/web/app/favicon.ico` in `orbit-ui-mobile` |
| `apple-touch-icon.png` | `apps/web/app/apple-icon.png` in `orbit-ui-mobile` |
| `orbit-icon-512.png` | `design/brand/png/orbit-platform-icon-512.png` in `orbit-ui-mobile` |
| `favicon.svg` | `design/brand/orbit-mascot-small.svg` in `orbit-ui-mobile`, plus the stylesheet below |
| `google-mark.svg` | `packages/shared/src/contracts/brand/GoogleMark.ts` in `orbit-ui-mobile` |
| `Sources/orbit-mark.svg` | `design/brand/orbit-mascot.svg` in `orbit-ui-mobile`, byte for byte |
| `orbit-bimi.svg` | `src/Orbit.Api/Branding/Sources/orbit-mark.svg` in this repository, through the generator |

`favicon.svg` is the small static face for 16, 20 and 24 pixels. It adds a standalone stylesheet after its description. An image cannot inherit the CSS of the page, so the stylesheet sets the foreground colour (`#1A1A1D` in light mode and `#F4F4F6` in dark mode), the `--primary` token (`#C4530F`) and the face tokens `--bg` and `--orbit-surface` (`#FAFAFA` in light mode and `#09090B` in dark mode). Only the favicon SVG response permits this stylesheet, through its computed CSP hash. Other non-OAuth responses keep their policy.

`google-mark.svg` copies the four paths, the fills, the 48px view box and the 20px display size from `GoogleMark.ts`. See the [Google branding guidelines](https://developers.google.com/identity/branding-guidelines).

A closed path map serves GET and HEAD for these images before auth, version enforcement and rate limiting. Security headers still apply. A stable discovery URL has a seven-day public cache without `immutable`, so a later brand update can expire.

## BIMI icon

`Assets/orbit-bimi.svg` is at `/bimi/orbit.svg` with the type `image/svg+xml`. GET returns the committed bytes. HEAD returns the same headers and no body.

`tools/generate-bimi-logo.mjs` builds the icon from `Sources/orbit-mark.svg`. It reads the `path` and `circle` shapes of the mascot in paint order. It turns each circle into four cubic curves, and it replaces each `var()` colour with a flat colour. It supports only the absolute commands M, L, C and Z, and it rejects other commands. It rejects a clip that a path leaves, because the icon draws no clip. The icon has a `#F4F4F6` planet and ring, a `#09090B` face, a `#C4530F` moon and a square `#09090B` background. The ink is centred at 60% of the canvas width.

```sh
node tools/generate-bimi-logo.mjs
node tools/generate-bimi-logo.mjs --check
node tools/generate-bimi-logo.mjs --source <path to design/brand/orbit-mascot.svg>
```

The `--source` command copies the mascot into `Sources/orbit-mark.svg` and then builds the icon.

The `--check` command fails when the generator does not reproduce the committed asset. When you change the mark, regenerate the icon. Then review the geometry and composition assertions in `BimiLogoTests` and run `node --test tools/__tests__/generate-bimi-logo.test.mjs`.

The icon follows the [Google SVG requirements](https://knowledge.workspace.google.com/admin/security/create-a-bimi-svg-file-detailed-steps) for BIMI. Run the focused tests:

```sh
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~BimiLogoTests|FullyQualifiedName~BrandDiscoveryTests'
```
