using System;

namespace ArenaGuard.Rules
{
    internal static class ArenaQueuePromptPolicy
    {
        internal static bool ShouldOpen(
            bool isCalledForLocalPlayer,
            string displayedSessionId,
            string incomingSessionId)
        {
            return isCalledForLocalPlayer &&
                   !string.IsNullOrWhiteSpace(incomingSessionId) &&
                   !string.Equals(displayedSessionId, incomingSessionId, StringComparison.Ordinal);
        }
    }
}
