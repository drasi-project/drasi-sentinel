namespace Drasi.Sentinel.Application;

public enum AgentRole
{
    Assistant,
    User,
}

public sealed record AgentMessage(AgentRole Role, string Text);

public interface ILanguageModel
{
    Task<string> CompleteAsync(
        string instructions,
        IAgentTools tools,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken);
}

public static class AgentMessages
{
    public static IReadOnlyList<AgentMessage> Create(IReadOnlyList<string> priorAssistantTexts, string input)
    {
        ArgumentNullException.ThrowIfNull(priorAssistantTexts);
        ArgumentNullException.ThrowIfNull(input);
        var messages = new List<AgentMessage>(priorAssistantTexts.Count + 1);
        foreach (var text in priorAssistantTexts)
            messages.Add(new AgentMessage(AgentRole.Assistant, text));
        messages.Add(new AgentMessage(AgentRole.User, input));
        return messages;
    }
}
