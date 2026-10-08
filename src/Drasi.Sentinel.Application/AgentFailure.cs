namespace Drasi.Sentinel.Application;

public enum AgentFailure
{
    CredentialUnavailable,
    ProviderUnavailable,
}

public sealed class AgentFailureException(AgentFailure failure, string message, Exception inner)
    : Exception(message, inner)
{
    public AgentFailure Failure { get; } = failure;
}
