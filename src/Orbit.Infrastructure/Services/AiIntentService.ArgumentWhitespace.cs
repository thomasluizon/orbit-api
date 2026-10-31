namespace Orbit.Infrastructure.Services;

public sealed partial class AiIntentService
{
    private sealed class FunctionArgumentStream(string toolName)
    {
        public string ToolName { get; } = toolName;
        private bool _inString;
        private bool _escaped;
        private int _whitespace;

        public bool HasDegenerated(string delta)
        {
            foreach (var character in delta)
            {
                if (_inString)
                {
                    if (_escaped) _escaped = false;
                    else if (character == '\\') _escaped = true;
                    else if (character == '"') _inString = false;
                    continue;
                }
                if (character is ' ' or '\t' or '\r' or '\n')
                {
                    if (++_whitespace > 256) return true;
                }
                else
                {
                    _whitespace = 0;
                    if (character == '"') _inString = true;
                }
            }
            return false;
        }
    }
}
