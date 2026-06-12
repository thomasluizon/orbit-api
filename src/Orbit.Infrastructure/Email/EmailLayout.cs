namespace Orbit.Infrastructure.Email;

/// <summary>
/// Layout-level values for the shared email chrome: document language, hidden
/// preview line, footer line, hosted logo URL, and whether the header band
/// renders the violet gradient (welcome email) or the plain canvas.
/// </summary>
public sealed record EmailLayout(
    string Lang,
    string Preheader,
    string Footer,
    string LogoUrl,
    bool GradientHeader);
