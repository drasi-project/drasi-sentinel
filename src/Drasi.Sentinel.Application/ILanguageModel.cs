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
    public static IReadOnlyList<AgentMessage> Create(IReadOnlyList<string> priorAssistantTexts, string input) =>
        Create(priorAssistantTexts.Select(text => new AgentMessage(AgentRole.Assistant, text)).ToArray(), input);

    public static IReadOnlyList<AgentMessage> Create(IReadOnlyList<AgentMessage> priorTurns, string input)
    {
        ArgumentNullException.ThrowIfNull(priorTurns);
        ArgumentNullException.ThrowIfNull(input);
        var messages = new List<AgentMessage>(priorTurns.Count + 1);
        messages.AddRange(priorTurns);
        messages.Add(new AgentMessage(AgentRole.User, input));
        return messages;
    }

    public static string CallerText(IReadOnlyList<AgentMessage> priorTurns, string input)
    {
        ArgumentNullException.ThrowIfNull(priorTurns);
        var caller = priorTurns
            .Where(message => message.Role == AgentRole.User)
            .Select(message => message.Text)
            .Append(input)
            .Where(text => !string.IsNullOrWhiteSpace(text));
        return string.Join("\n", caller);
    }
}
