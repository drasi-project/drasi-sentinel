using Drasi.Sentinel.Application;

namespace Drasi.Sentinel.Agent.Maf;

public enum ModelCredentialKind
{
    AzureCli,
    DefaultAzure,
}

public static class MafLanguageModel
{
    public static ILanguageModel Create(Uri endpoint, string model, ModelCredentialKind credential) =>
        new AzureOpenAiLanguageModel(endpoint, model, credential);
}
