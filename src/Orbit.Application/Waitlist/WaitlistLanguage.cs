namespace Orbit.Application.Waitlist;

public static class WaitlistLanguage
{
    public static bool TryCanonicalize(string? language, out string canonical)
    {
        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
        {
            canonical = "en";
            return true;
        }

        if (string.Equals(language, "pt-BR", StringComparison.OrdinalIgnoreCase))
        {
            canonical = "pt-BR";
            return true;
        }

        canonical = string.Empty;
        return false;
    }
}
