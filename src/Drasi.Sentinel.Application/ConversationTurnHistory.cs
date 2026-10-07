using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public static class ConversationTurnHistory
{
    public const int PageSize = 100;

    public static async Task<IReadOnlyList<string>> LoadAssistantTextsAsync(
        IConversationRepository repository,
        OwnerId owner,
        ConversationTaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var latest = await repository.ReadLatestEventSequenceAsync(owner, taskId, cancellationToken);
        if (latest.Value == 0)
            return [];

        return await LoadAssistantTextsAsync(
            (after, limit, token) => repository.ReadEventsAsync(owner, taskId, after, limit, token),
            cancellationToken,
            PageSize,
            latest);
    }

    public static async Task<IReadOnlyList<string>> LoadAssistantTextsAsync(
        Func<TaskEventSequence, int, CancellationToken, Task<IReadOnlyList<ConversationEvent>>> readPage,
        CancellationToken cancellationToken,
        int pageSize,
        TaskEventSequence inclusiveUpperBound)
    {
        ArgumentNullException.ThrowIfNull(readPage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        if (inclusiveUpperBound.Value <= 0)
            return [];

        var texts = new List<string>();
        var after = new TaskEventSequence(0);
        while (after.Value < inclusiveUpperBound.Value)
        {
            var page = await readPage(after, pageSize, cancellationToken);
            if (page.Count == 0)
                return texts;

            var last = after.Value;
            foreach (var item in page)
            {
                if (item.Sequence.Value <= after.Value)
                    continue;
                if (item.Sequence.Value > inclusiveUpperBound.Value)
                    return texts;
                last = item.Sequence.Value;
                if (item.Kind == ConversationEventKind.AssistantArtifact)
                    texts.Add(item.Content);
            }

            if (last <= after.Value || page.Count < pageSize)
                return texts;

            after = new TaskEventSequence(last);
        }

        return texts;
    }
}
