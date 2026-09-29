namespace Orbit.Api.OAuth;

/// <summary>
/// The authorize page's one style sheet and its one drawn graphic. Every colour, radius, size and space is
/// a token copied from the design system, so the page holds no literal outside the token block, and the
/// mark is the shipped accent treatment rather than a tinted copy of the monochrome file.
/// </summary>
public static class OAuthPageStyles
{
    /// <summary>The orbital mark, accent on the moon and <c>--fg-1</c> everywhere else.</summary>
    public const string Mark = """
<svg class="mark" data-orbit-mark="accent" viewBox="0 0 1024 1024" width="48" height="48" fill="none" role="img" aria-labelledby="orbit-mark-title" focusable="false">
<title id="orbit-mark-title">Orbit</title>
<path fill-rule="evenodd" fill="currentColor" d="M498.347 358.729C539.875 357.831 583.698 377.184 610.586 408.533C634.588 436.517 624.997 437.281 661.874 425.937C676.533 421.448 691.275 417.233 706.091 413.295C723.902 408.634 777.785 394.843 793.574 404.702C795.533 405.925 797.258 407.672 797.705 410.012C798.217 412.696 797.329 415.656 796.258 418.095C785.372 442.873 711.309 486.059 685.864 500.405C677.068 505.364 658.941 513.54 652.59 520.037C645.667 527.119 643.89 546.344 640.171 556.264C636.069 567.256 630.758 577.758 624.338 587.578C602.341 620.72 568.121 643.81 529.155 651.803C491.019 659.282 451.474 651.294 419.231 629.599C433.293 625.131 447.364 619.053 461.308 614.044C490.361 624.262 515.473 624.017 544.23 613.011C552.905 609.734 563.834 602.972 571.084 597.288C583.691 587.401 594.145 575.042 601.802 560.969C604.397 556.243 609.801 546.94 603.045 544.001C602.447 543.74 597.324 545.968 596.598 546.313C587.225 550.766 577.833 555.191 568.434 559.593C520.948 581.727 472.588 601.935 423.473 620.168C389.51 632.993 354.928 644.112 319.855 653.483C298.916 658.871 276.913 664.241 255.263 665.369C247.767 665.759 235.005 666.404 229.146 660.776C227.425 659.123 226.54 657.02 226.527 654.639C226.412 633.678 295.057 592.302 311.105 582.716C320.542 577.08 339.324 567.887 346.131 560.506C358.47 547.125 353.427 529.78 352.666 513.775C350.963 474.547 364.927 436.251 391.481 407.325C420.227 376.027 456.02 360.456 498.347 358.729Z M 468 396 L 470 396 L 472 397 L 474 397 L 476 397 L 478 397 L 480 397 L 482 397 L 484 397 L 486 397 L 488 397 L 490 397 L 492 397 L 494 397 L 496 396 L 498 396 L 500 396 L 502 396 L 504 396 L 506 396 L 508 396 L 510 396 L 512 396 L 514 396 L 516 396 L 518 397 L 520 397 L 522 397 L 524 397 L 526 398 L 528 398 L 530 399 L 532 399 L 534 400 L 536 400 L 538 401 L 540 402 L 542 402 L 544 403 L 546 404 L 548 405 L 550 405 L 552 406 L 554 407 L 556 408 L 558 409 L 560 411 L 562 412 L 564 413 L 566 414 L 568 416 L 570 417 L 572 419 L 574 420 L 576 422 L 578 423 L 580 425 L 582 427 L 584 429 L 586 431 L 588 433 L 589 435 L 591 437 L 592 439 L 594 441 L 595 443 L 597 445 L 598 447 L 599 449 L 600 451 L 601 453 L 603 455 L 604 457 L 605 459 L 605 461 L 606 463 L 607 465 L 608 467 L 609 469 L 609 471 L 610 473 L 610 475 L 611 477 L 611 479 L 612 481 L 612 483 L 613 485 L 613 487 L 613 489 L 614 491 L 614 493 L 614 495 L 614 497 L 614 499 L 614 501 L 614 503 L 615 505 L 617 505 L 619 505 L 621 505 L 623 505 L 625 505 L 627 505 L 629 505 L 631 505 L 633 505 L 635 504 L 637 502 L 639 500 L 641 498 L 642 496 L 644 494 L 644 492 L 645 490 L 645 488 L 645 486 L 645 484 L 645 482 L 645 480 L 645 478 L 645 476 L 645 474 L 645 472 L 644 470 L 644 468 L 643 466 L 643 464 L 642 462 L 641 460 L 641 458 L 640 456 L 640 454 L 640 452 L 640 450 L 642 448 L 644 447 L 646 446 L 648 445 L 650 444 L 652 444 L 654 443 L 656 443 L 658 443 L 660 442 L 662 442 L 664 441 L 666 441 L 668 441 L 670 440 L 672 440 L 674 439 L 676 439 L 678 438 L 680 438 L 682 438 L 684 437 L 686 437 L 688 437 L 690 437 L 692 436 L 694 436 L 696 436 L 698 436 L 700 436 L 702 436 L 704 436 L 706 436 L 708 436 L 710 436 L 712 436 L 714 437 L 716 438 L 718 439 L 720 440 L 721 442 L 721 444 L 720 446 L 720 448 L 719 450 L 717 452 L 716 454 L 714 456 L 713 458 L 711 460 L 709 462 L 707 464 L 705 465 L 703 467 L 701 469 L 699 471 L 697 472 L 695 474 L 693 475 L 691 477 L 689 478 L 687 480 L 685 481 L 683 482 L 681 484 L 679 485 L 677 486 L 675 488 L 673 489 L 671 490 L 669 492 L 667 493 L 665 494 L 663 495 L 661 497 L 659 498 L 657 499 L 655 500 L 653 501 L 651 503 L 649 504 L 647 505 L 645 506 L 643 507 L 641 508 L 639 510 L 637 511 L 635 512 L 633 513 L 631 514 L 629 515 L 627 516 L 625 517 L 623 518 L 621 520 L 619 521 L 617 522 L 615 523 L 613 524 L 611 525 L 609 526 L 607 527 L 605 528 L 603 529 L 601 530 L 599 531 L 597 532 L 595 533 L 593 534 L 591 535 L 589 536 L 587 537 L 585 538 L 583 539 L 581 540 L 579 541 L 577 542 L 575 543 L 573 544 L 571 545 L 569 546 L 567 547 L 565 548 L 563 549 L 561 550 L 559 551 L 557 552 L 555 553 L 553 554 L 551 555 L 549 556 L 547 557 L 545 557 L 543 558 L 541 559 L 539 560 L 537 561 L 535 562 L 533 563 L 531 564 L 529 565 L 527 566 L 525 567 L 523 567 L 521 568 L 519 569 L 517 570 L 515 571 L 513 572 L 511 573 L 509 573 L 507 574 L 505 575 L 503 576 L 501 577 L 499 577 L 497 578 L 495 579 L 493 580 L 491 581 L 489 581 L 487 582 L 485 583 L 483 584 L 481 584 L 479 585 L 477 586 L 475 587 L 473 587 L 471 588 L 469 588 L 467 589 L 465 589 L 463 590 L 461 590 L 459 590 L 457 591 L 455 591 L 453 591 L 451 591 L 449 591 L 447 591 L 445 590 L 443 590 L 441 590 L 439 589 L 437 589 L 435 588 L 433 588 L 431 587 L 429 586 L 427 585 L 425 584 L 423 583 L 421 582 L 419 581 L 417 579 L 415 578 L 413 576 L 411 575 L 409 573 L 407 571 L 406 569 L 404 567 L 403 565 L 401 563 L 400 561 L 399 559 L 398 557 L 397 555 L 396 553 L 395 551 L 394 549 L 393 547 L 392 545 L 392 543 L 391 541 L 390 539 L 390 537 L 389 535 L 389 533 L 388 531 L 388 529 L 387 527 L 387 525 L 387 523 L 386 521 L 386 519 L 386 517 L 386 515 L 386 513 L 386 511 L 386 509 L 386 507 L 386 505 L 386 503 L 386 501 L 386 499 L 386 497 L 386 495 L 386 493 L 387 491 L 387 489 L 387 487 L 387 485 L 388 483 L 388 481 L 389 479 L 389 477 L 390 475 L 390 473 L 391 471 L 392 469 L 392 467 L 393 465 L 394 463 L 395 461 L 396 459 L 397 457 L 398 455 L 399 453 L 400 451 L 401 449 L 402 447 L 403 445 L 405 443 L 406 441 L 407 439 L 409 437 L 411 435 L 412 433 L 414 431 L 416 429 L 418 427 L 420 425 L 422 423 L 424 421 L 426 419 L 428 418 L 430 416 L 432 415 L 434 413 L 436 412 L 438 410 L 440 409 L 442 408 L 444 407 L 446 406 L 448 405 L 450 404 L 452 403 L 454 402 L 456 401 L 458 400 L 460 399 L 462 398 L 464 398 L 466 397 Z M360.022 571.067C370.208 571.112 371.163 579.24 375.934 586.395C378.626 590.432 382.108 594.464 384.382 598.688C385.459 600.656 386.162 602.806 386.455 605.03C386.957 609.029 386.195 612.976 383.584 616.134C376.306 624.939 340.86 629.091 328.62 630.425C320.097 630.776 296.678 631.941 303.915 617.416C311.174 602.846 332.876 589.124 346.091 579.666C349.696 577.085 356.234 572.533 360.022 571.067Z"/>
<path fill="var(--primary, currentColor)" d="M650.232 366.84C662.457 366.308 672.837 375.703 673.526 387.919C674.214 400.136 664.954 410.637 652.747 411.482C644.71 412.038 636.995 408.227 632.552 401.507C628.109 394.787 627.625 386.196 631.285 379.019C634.945 371.842 642.183 367.19 650.232 366.84Z"/>
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
  --mark-size: 48px;
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
  }
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
.mark { display: block; width: var(--mark-size); height: var(--mark-size); color: var(--fg-1); }
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
.btn:disabled { opacity: 0.4; cursor: not-allowed; }
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
@media (hover: hover) and (pointer: fine) {
  .btn-primary:hover:not(:disabled) { background: var(--primary-hover); }
  .btn-ghost:hover:not(:disabled) { background: var(--bg-card); }
}
.btn-primary:active:not(:disabled) { background: var(--primary-pressed); transform: scale(0.96); }
.btn-ghost:active:not(:disabled) { transform: scale(0.96); }
.divider { display: flex; align-items: center; gap: var(--s-3); }
.divider span[data-rule] { flex: 1; height: 1px; background: var(--hairline); }
.divider span[data-label] { font-family: var(--font-mono); font-size: var(--fs-xs); color: var(--fg-4); }
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
.cell[data-active] { box-shadow: inset 0 0 0 2px var(--primary); outline: 2px solid var(--primary); outline-offset: 2px; }
.code[data-error] .cell { box-shadow: inset 0 0 0 2px var(--status-bad); }
.code[data-disabled] { opacity: 0.4; }
.scopes {
  display: flex;
  flex-direction: column;
  gap: var(--s-2);
}
.scopes p { font-size: var(--fs-sm); color: var(--fg-2); }
.scopes ul { display: flex; flex-direction: column; gap: var(--s-1); padding-left: var(--s-5); }
.scopes li { font-size: var(--fs-sm); color: var(--fg-3); text-wrap: pretty; }
.alert { font-size: var(--fs-sm); color: var(--status-bad-text); text-wrap: pretty; }
.hidden { display: none; }
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { transition-duration: 0.01ms !important; animation-duration: 0.01ms !important; }
}
""";
}
