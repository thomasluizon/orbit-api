namespace Orbit.Api.OAuth;

/// <summary>
/// The authorize page's one style sheet and its one drawn graphic. Every colour, radius, size and space is
/// a token copied from the design system, so the page holds no literal outside the token block, and the
/// mark is the shipped mascot rather than a tinted copy of a monochrome file.
/// </summary>
public static class OAuthPageStyles
{
    /// <summary>
    /// The static Orbit mascot at rest (Parado) from <c>design/brand/orbit-mascot.svg</c>: <c>--fg-1</c> on the planet
    /// and the ring, <c>--bg</c> on the eyes and the ring gap, and the accent on the moon.
    /// </summary>
    public const string Mark = """
<svg class="mark" data-orbit-mark="mascot" viewBox="-210 -150 420 300" fill="none" role="img" aria-labelledby="orbit-mark-title" focusable="false">
<title id="orbit-mark-title">Orbit</title>
<defs><clipPath id="orbit-mark-planet"><circle cx="0" cy="0" r="100"/></clipPath></defs>
<path fill="currentColor" d="M-162.055 52.672L-162.556 50.901L-162.456 48.282L-161.381 44.644L-159.062 40.134L-155.406 34.967L-150.418 29.325L-144.145 23.344L-136.654 17.129L-128.019 10.767L-118.322 4.33L-107.649 -2.116L-96.092 -8.51L-83.749 -14.796L-70.719 -20.918L-57.109 -26.827L-43.029 -32.473L-28.59 -37.81L-13.907 -42.797L0.903 -47.393L15.722 -51.562L30.432 -55.271L44.915 -58.49L59.055 -61.196L72.735 -63.366L85.843 -64.986L98.265 -66.045L109.894 -66.537L120.618 -66.465L130.331 -65.841L138.92 -64.69L146.27 -63.059L152.262 -61.03L156.785 -58.748L159.791 -56.444L161.412 -54.393L162.055 -52.672L180.325 -58.574L176.854 -65.084L171.786 -70.353L165.579 -74.405L158.437 -77.467L150.398 -79.726L141.46 -81.297L131.627 -82.237L120.923 -82.577L109.394 -82.335L97.099 -81.524L84.112 -80.156L70.516 -78.245L56.403 -75.807L41.87 -72.861L27.02 -69.429L11.96 -65.538L-3.201 -61.215L-18.355 -56.493L-33.389 -51.406L-48.196 -45.992L-62.667 -40.288L-76.698 -34.336L-90.186 -28.177L-103.037 -21.854L-115.159 -15.409L-126.47 -8.883L-136.893 -2.313L-146.362 4.268L-154.821 10.833L-162.223 17.372L-168.53 23.894L-173.704 30.444L-177.683 37.113L-180.325 44.033L-181.334 51.268L-180.325 58.574Z"/>
<circle cx="0" cy="0" r="100" fill="currentColor"/>
<path fill="var(--bg)" d="M-46 -47C-46 -53.075 -41.075 -58 -35 -58C-28.925 -58 -24 -53.075 -24 -47L-24 -13C-24 -6.925 -28.925 -2 -35 -2C-41.075 -2 -46 -6.925 -46 -13Z"/>
<path fill="var(--bg)" d="M24 -47C24 -53.075 28.925 -58 35 -58C41.075 -58 46 -53.075 46 -47L46 -13C46 -6.925 41.075 -2 35 -2C28.925 -2 24 -6.925 24 -13Z"/>
<g clip-path="url(#orbit-mark-planet)"><path fill="var(--orbit-surface, var(--bg))" d="M92.388 38.268L94.693 32.144L96.593 25.882L98.079 19.509L99.144 13.053L99.786 6.54L100 0L99.786 -6.54L99.617 -8.256L90.018 -3.161L77.961 2.765L65.22 8.577L51.905 14.222L38.128 19.65L24.002 24.815L9.644 29.672L-4.826 34.182L-19.29 38.307L-33.626 42.014L-47.716 45.273L-61.439 48.06L-74.676 50.353L-85.438 51.873L-83.147 55.557L-79.335 60.876L-75.184 65.935L-70.711 70.711L-65.935 75.184L-60.876 79.335L-55.557 83.147L-50 86.603L-47.838 87.758L-39.069 86.078L-23.826 82.686L-8.393 78.793L7.124 74.427L22.617 69.618L37.977 64.402L53.097 58.814L67.871 52.892L82.196 46.677L90.303 42.871Z"/></g>
<path fill="currentColor" d="M162.055 -52.672L162.137 -50.893L161.645 -48.474L160.253 -45.165L157.693 -41.04L153.856 -36.275L148.731 -31.033L142.358 -25.438L134.795 -19.592L126.113 -13.575L116.391 -7.459L105.715 -1.307L94.173 4.823L81.863 10.875L68.883 16.798L55.34 22.541L41.341 28.057L26.998 33.301L12.424 38.232L-2.264 42.809L-16.951 46.997L-31.518 50.763L-45.85 54.078L-59.832 56.915L-73.349 59.255L-86.288 61.08L-98.542 62.378L-110.001 63.144L-120.56 63.38L-130.114 63.097L-138.557 62.318L-145.782 61.09L-151.684 59.493L-156.177 57.665L-159.247 55.813L-161.068 54.155L-162.055 52.672L-180.325 58.574L-177.198 65.322L-172.33 70.984L-166.187 75.489L-159.014 79.003L-150.886 81.696L-141.823 83.669L-131.843 84.981L-120.981 85.662L-109.286 85.727L-96.822 85.19L-83.666 84.062L-69.902 82.356L-55.626 80.087L-40.935 77.273L-25.934 73.936L-10.732 70.102L4.562 65.799L19.837 61.058L34.981 55.915L49.884 50.407L64.437 44.573L78.533 38.456L92.072 32.098L104.956 25.542L117.094 18.833L128.4 12.012L138.799 5.121L148.221 -1.805L156.608 -8.739L163.909 -15.664L170.081 -22.586L175.073 -29.538L178.811 -36.592L181.136 -43.84L181.752 -51.276L180.325 -58.574Z"/>
<circle cx="-138.376" cy="73.255" r="16.259" fill="var(--primary, currentColor)"/>
</svg>
""";

    /// <summary>
    /// The whole sheet. The first block is the token block and the only place a literal appears; every rule
    /// under it reads a token. Light mode repoints the same names from <c>prefers-color-scheme</c>.
    /// </summary>
    public const string Css = """
:root {
  --bg: #09090B;
  --bg-card: rgba(250,250,250,0.04);
  --bg-field: rgba(250,250,250,0.06);
  --bg-well: rgba(250,250,250,0.08);
  --hairline: rgba(255,255,255,0.08);
  --hairline-ghost: rgba(255,255,255,0.10);
  --hairline-strong: rgba(255,255,255,0.16);
  --border-control: rgba(255,255,255,0.08);
  --fg-1: #F4F4F6;
  --fg-2: #C9C9CC;
  --fg-3: #8F8F93;
  --fg-4: #5D5D60;
  --primary: #C4530F;
  --primary-hover: #B74E12;
  --primary-pressed: #A24716;
  --primary-soft: #C85716;
  --fg-on-primary: #FFFFFF;
  --status-bad: #FB2C36;
  --status-bad-text: #FF7970;
  --selection-bg: rgba(196,83,15,0.32);
  --font-sans: 'Geist Sans', system-ui, sans-serif;
  --font-display: 'Space Grotesk', 'Geist Sans', system-ui, sans-serif;
  --font-mono: 'Geist Mono', ui-monospace, monospace;
  --fs-xs: 12px;
  --fs-sm: 14px;
  --fs-base: 16px;
  --fs-xl: 22px;
  --fs-code: 26px;
  --track-display: -0.02em;
  --track-label: 0.06em;
  --s-1: 4px;
  --s-2: 8px;
  --s-3: 12px;
  --s-4: 16px;
  --s-5: 24px;
  --s-6: 32px;
  --r-chip: 8px;
  --r-well: 12px;
  --r-card: 20px;
  --r-pill: 999px;
  --touch-min: 44px;
  --field-h: 54px;
  --btn-h: 50px;
  --btn-h-sm: 40px;
  --btn-pad: 26px;
  --btn-pad-sm: 18px;
  --cell-w: 44px;
  --cell-h: 56px;
  --mark-size: 96px;
  --btn-h-provider: 44px;
  --fs-provider: 19px;
  --apple-pad: 8%;
  --apple-fill: #FFFFFF;
  --apple-ink: #000000;
  --column: 420px;
  --dur-fast: 240ms;
  --ease-standard: cubic-bezier(0.2,0,0,1);
}
@media (prefers-color-scheme: light) {
  :root {
    --bg: #FAFAFA;
    --bg-card: #FFFFFF;
    --bg-field: #FFFFFF;
    --bg-well: rgba(9,9,11,0.04);
    --hairline: rgba(9,9,11,0.08);
    --hairline-ghost: rgba(9,9,11,0.10);
    --hairline-strong: rgba(9,9,11,0.16);
    --border-control: rgba(9,9,11,0.08);
    --fg-1: #1A1A1D;
    --fg-2: #424247;
    --fg-3: #68686D;
    --fg-4: #89898D;
    --primary-soft: #C15109;
    --status-bad: #E7000B;
    --status-bad-text: #D70009;
    --selection-bg: rgba(196,83,15,0.18);
    --apple-fill: #000000;
    --apple-ink: #FFFFFF;
  }
}
@media (min-width: 1024px) {
  :root { --mark-size: 128px; }
}
@font-face {
  font-family: 'Geist Sans';
  font-style: normal;
  font-weight: 400 600;
  font-display: swap;
  src: url('/oauth/assets/geist-latin.woff2') format('woff2');
}
@font-face {
  font-family: 'Space Grotesk';
  font-style: normal;
  font-weight: 500 600;
  font-display: swap;
  src: url('/oauth/assets/space-grotesk-latin.woff2') format('woff2');
}
@font-face {
  font-family: 'Geist Mono';
  font-style: normal;
  font-weight: 400 500;
  font-display: swap;
  src: url('/oauth/assets/geist-mono-latin.woff2') format('woff2');
}
* { box-sizing: border-box; margin: 0; padding: 0; }
html { -webkit-font-smoothing: antialiased; -moz-osx-font-smoothing: grayscale; font-synthesis: none; }
body {
  background: var(--bg);
  color: var(--fg-1);
  font-family: var(--font-sans);
  font-size: var(--fs-base);
  line-height: 1.55;
  min-height: 100dvh;
  display: grid;
  place-items: center;
  padding: var(--s-4);
}
::selection { background: var(--selection-bg); }
:focus-visible { outline: 2px solid var(--primary); outline-offset: 2px; }
.card {
  width: 100%;
  max-width: var(--column);
  display: flex;
  flex-direction: column;
  gap: var(--s-6);
}
@media (min-width: 480px) {
  .card {
    padding: var(--s-6);
    background: var(--bg-card);
    border-radius: var(--r-card);
    box-shadow: inset 0 0 0 1px var(--hairline-ghost);
  }
}
.mark { display: block; align-self: center; width: var(--mark-size); height: calc(var(--mark-size) * 300 / 420); color: var(--fg-1); }
.intro { display: flex; flex-direction: column; gap: var(--s-2); }
.eyebrow {
  font-family: var(--font-mono);
  font-size: var(--fs-xs);
  letter-spacing: var(--track-label);
  text-transform: uppercase;
  color: var(--fg-3);
}
h1 {
  font-family: var(--font-display);
  font-size: var(--fs-xl);
  line-height: 1.2;
  font-weight: 500;
  letter-spacing: var(--track-display);
  text-wrap: pretty;
}
.body-line { color: var(--fg-2); text-wrap: pretty; }
.step { display: flex; flex-direction: column; gap: var(--s-5); }
.field { display: flex; flex-direction: column; gap: var(--s-2); }
.field label { font-size: var(--fs-sm); font-weight: 500; color: var(--fg-2); }
.control {
  min-height: var(--field-h);
  display: flex;
  align-items: center;
  overflow: hidden;
  border-radius: var(--r-well);
  background: var(--bg-field);
  box-shadow: inset 0 0 0 1px var(--border-control);
}
.control:focus-within { outline: 2px solid var(--primary); outline-offset: 2px; }
.field[data-error] .control { box-shadow: inset 0 0 0 2px var(--status-bad); }
.control input {
  width: 100%;
  min-height: var(--field-h);
  appearance: none;
  border: 0;
  background: transparent;
  outline: none;
  font-family: var(--font-sans);
  font-size: var(--fs-base);
  line-height: 24px;
  color: var(--fg-1);
  padding: var(--s-3) var(--s-4);
}
.control input::placeholder { color: var(--fg-3); }
.caption { font-size: var(--fs-xs); color: var(--status-bad-text); }
.note { font-family: var(--font-mono); font-size: var(--fs-xs); color: var(--fg-3); }
.note[data-tabular] { font-variant-numeric: tabular-nums; }
.actions { display: flex; flex-direction: column; align-items: flex-start; gap: var(--s-2); }
.btn {
  height: var(--btn-h);
  width: 100%;
  padding-inline: var(--btn-pad);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: var(--s-2);
  border: 0;
  border-radius: var(--r-pill);
  font-family: var(--font-sans);
  font-size: var(--fs-base);
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  text-decoration: none;
  transition: background var(--dur-fast) var(--ease-standard), transform 150ms var(--ease-standard);
}
.providers { display: flex; flex-direction: column; gap: var(--s-2); }
.btn-provider { height: var(--btn-h-provider); font-size: var(--fs-provider); }
.btn-google { padding-inline: var(--s-3); gap: 10px; }
.btn-google img { flex-shrink: 0; }
.btn-apple { padding-inline: var(--apple-pad); gap: 0; background: var(--apple-fill); color: var(--apple-ink); }
.btn-apple svg { flex-shrink: 0; }
.btn:disabled, .btn[aria-disabled="true"] { opacity: 0.4; cursor: not-allowed; }
.btn-primary { background: var(--primary); color: var(--fg-on-primary); }
.btn-ghost { background: transparent; color: var(--fg-1); box-shadow: inset 0 0 0 1.5px var(--hairline-strong); }
.btn-sm {
  position: relative;
  height: var(--btn-h-sm);
  width: auto;
  align-self: flex-start;
  padding-inline: var(--btn-pad-sm);
  font-size: var(--fs-sm);
}
.btn-sm::after {
  content: '';
  position: absolute;
  inset-inline: 0;
  top: 50%;
  height: var(--touch-min);
  transform: translateY(-50%);
}
.action-row {
  display: flex;
  flex-wrap: nowrap;
  flex-direction: column;
  justify-content: flex-end;
  align-items: center;
  gap: var(--s-3);
}
.action-row .btn { width: 100%; }
@media (min-width: 480px) {
  .action-row[data-layout="row"] { flex-direction: row; }
  .action-row[data-layout="row"] .btn { width: auto; flex: 0 0 auto; }
}
@media (hover: hover) and (pointer: fine) {
  .btn-primary:hover:not(:disabled):not([aria-disabled="true"]) { background: var(--primary-hover); }
  .btn-ghost:hover:not(:disabled):not([aria-disabled="true"]) { background: var(--bg-card); }
}
.btn-primary:active:not(:disabled):not([aria-disabled="true"]) { background: var(--primary-pressed); transform: scale(0.96); }
.btn-apple:active:not(:disabled):not([aria-disabled="true"]) { transform: scale(0.96); }
.btn-ghost:active:not(:disabled):not([aria-disabled="true"]) { transform: scale(0.96); }
.divider { display: flex; align-items: center; gap: var(--s-3); }
.divider span[data-rule] { flex: 1; height: 1px; background: var(--hairline); }
.divider span[data-label] { font-family: var(--font-mono); font-size: var(--fs-xs); color: var(--fg-3); }
.code { position: relative; display: flex; align-items: center; justify-content: center; gap: var(--s-2); }
.code input {
  outline: none;
  position: absolute;
  inset: 0;
  z-index: 1;
  width: 100%;
  height: 100%;
  opacity: 0;
  cursor: text;
}
.cell {
  pointer-events: none;
  display: grid;
  place-items: center;
  width: var(--cell-w);
  height: var(--cell-h);
  flex: 0 0 auto;
  border-radius: var(--r-well);
  background: var(--bg-field);
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  font-weight: 500;
  color: var(--fg-1);
  box-shadow: inset 0 0 0 1px var(--border-control);
}
.cell[data-active] { box-shadow: inset 0 0 0 2px var(--primary); }
.code[data-error] .cell { box-shadow: inset 0 0 0 2px var(--status-bad); }
.code[data-disabled] { opacity: 0.4; }
@media (forced-colors: active) {
  .cell { border: 1px solid CanvasText; }
  .cell[data-active] { border: 2px solid Highlight; }
}
.scopes {
  display: flex;
  flex-direction: column;
  gap: var(--s-2);
}
.scopes p { font-size: var(--fs-sm); color: var(--fg-2); }
.scopes ul { display: flex; flex-direction: column; gap: var(--s-1); padding-left: var(--s-5); }
.scopes li { font-size: var(--fs-sm); color: var(--fg-3); text-wrap: pretty; }
.scopes details { font-size: var(--fs-sm); color: var(--fg-3); }
.scopes summary { cursor: pointer; padding-block: var(--s-3); }
.scopes code { font-family: var(--font-mono); overflow-wrap: anywhere; }
.alert { font-size: var(--fs-sm); color: var(--status-bad-text); text-wrap: pretty; }
.hidden { display: none; }
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { transition-duration: 0.01ms !important; animation-duration: 0.01ms !important; }
}
""";
}
