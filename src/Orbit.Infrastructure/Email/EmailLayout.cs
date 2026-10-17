namespace Orbit.Infrastructure.Email;

/// <summary>
/// Layout-level values for the shared email chrome: document language, hidden
/// preview line, footer line, and the hosted logo URL. The header band carries no
/// per-email treatment: DESIGN.md bans the gradient wash the welcome and waitlist
/// emails used to render.
/// </summary>
public sealed record EmailLayout(
    string Lang,
    string Preheader,
    string Footer,
    string LogoUrl);
