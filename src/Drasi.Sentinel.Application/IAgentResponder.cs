using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public interface IAgentResponder
{
    Task<string> RespondAsync(
        Principal principal,
        TaskTurnId turn,
        string input,
        IReadOnlyList<string> priorAssistantTexts,
        CancellationToken cancellationToken);
}
