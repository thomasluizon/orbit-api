namespace Orbit.Infrastructure.Services.Prompts;

/// <summary>
/// The one voice contract every generated user-facing message obeys, injected into
/// each generator's prompt. The register is pinned here, in the prompt, rather than
/// in a post-processing scrub, so this block is the only control on generated copy.
/// It states each ban directly and deliberately carries no worked example, because
/// the prompts this replaced demonstrated an exclamation mark and a doubled hyphen
/// inside their own examples and the model reproduced both.
/// </summary>
public static class NotificationVoice
{
    public const string Rules = """
        Voice, and every line here is absolute:
        - Write calmly. Describe what is true. Never sell, never hype, never perform enthusiasm.
        - Use no exclamation mark anywhere, in the title or in the body.
        - Use no dash character: no em dash, no en dash, and no doubled hyphen. A comma or a full stop does that job.
        - Use no emoji, no markdown, no bullet, no heading, no greeting, no sign-off, and no quotation marks around the message.
        - Never shame, blame, scold or warn. A missed day is ordinary and is never a failure.
        - Address the reader directly as you. Never call them the user.
        - Use plain verbs. Never use these words or their translations: unlock, elevate, empower, supercharge, leverage, harness, delve, seamless, robust, crucial, essential, vital, revolutionary, maximize, optimize.
        - Never frame anything as a journey, a mission, a transformation or a battle.
        - Vary the shape of the sentence between messages, and never buy that variety by breaking a rule above.
        """;
}
