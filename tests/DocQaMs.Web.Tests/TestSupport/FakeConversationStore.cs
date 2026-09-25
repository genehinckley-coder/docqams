using DocQaMs.Core.Answering;
using DocQaMs.Core.Conversations;

namespace DocQaMs.Web.Tests.TestSupport;

/// <summary>
/// A conversation store whose snapshots the test sets. <see cref="Stop"/>, <see cref="Create"/>,
/// <see cref="TryChangeSettings"/>, <see cref="Ask"/> and <see cref="TakeReturnedQuestion"/> behave as the real
/// store's do, and record their calls.
/// </summary>
internal sealed class FakeConversationStore : IConversationStore
{
    private readonly Dictionary<Guid, ConversationSnapshot> _snapshots = [];
    private readonly List<Guid> _order = [];

    public event Action<Guid>? Changed;

    public ConversationCost TotalCost { get; private set; } = ConversationCost.Zero;

    /// <summary>Every call to <see cref="Stop"/>, in order.</summary>
    public List<Guid> StopCalls { get; } = [];

    /// <summary>The settings of every call to <see cref="Create"/>, in order.</summary>
    public List<AnswerSettings?> CreateCalls { get; } = [];

    /// <summary>Every call to <see cref="TryChangeSettings"/>, in order.</summary>
    public List<(Guid Id, AnswerSettings Settings)> SettingsChanges { get; } = [];

    /// <summary>Every call to <see cref="Ask"/>, in order.</summary>
    public List<(Guid Id, string Question)> AskCalls { get; } = [];

    /// <summary>How many times <see cref="TakeReturnedQuestion"/> returned a question.</summary>
    public int Takes { get; private set; }

    /// <summary>When set, <see cref="Ask"/> refuses with this reason.</summary>
    public string? RefuseWith { get; set; }

    public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Newest first, as the real store lists them.</summary>
    public IReadOnlyList<ConversationSnapshot> List() => [.. Enumerable.Reverse(_order).Select(id => _snapshots[id])];

    public ConversationSnapshot? Get(Guid id) => _snapshots.GetValueOrDefault(id);

    public ConversationSnapshot Create(AnswerSettings? settings = null)
    {
        CreateCalls.Add(settings);
        ConversationSnapshot snapshot = Idle(Guid.NewGuid()) with { Settings = settings ?? AnswerSettings.Default };
        Publish(snapshot);
        return snapshot;
    }

    public bool TryChangeSettings(Guid id, AnswerSettings settings)
    {
        SettingsChanges.Add((id, settings));
        if (Get(id) is not { SettingsLocked: false } snapshot)
        {
            return false;
        }

        Publish(snapshot with { Settings = settings });
        return true;
    }

    /// <summary>As the real store: the answer is in flight at once, and the returned question is cleared.</summary>
    public AskResult Ask(Guid id, string question)
    {
        AskCalls.Add((id, question));
        if (RefuseWith is { } reason)
        {
            return AskResult.Refused(reason);
        }

        if (Get(id) is not { IsAnswering: false } snapshot)
        {
            return AskResult.Refused("An answer is already in progress in this conversation. Wait for it, or stop it.");
        }

        Publish(snapshot with
        {
            SettingsLocked = true,
            ReturnedQuestion = null,
            InProgress = Answering(id).InProgress! with { Question = question.Trim(), Settings = snapshot.Settings },
        });
        return AskResult.Started(Task.CompletedTask);
    }

    public string? TakeReturnedQuestion(Guid id)
    {
        if (Get(id) is not { ReturnedQuestion: { } question } snapshot)
        {
            return null;
        }

        Takes++;
        Publish(snapshot with { ReturnedQuestion = null });
        return question;
    }

    /// <summary>As the real store: marks the answer in flight as stopping and raises the change; otherwise a no-op.</summary>
    public void Stop(Guid id)
    {
        StopCalls.Add(id);
        if (Get(id) is { InProgress: { IsStopping: false } inProgress } snapshot)
        {
            Publish(snapshot with { InProgress = inProgress with { IsStopping = true } });
        }
    }

    /// <summary>Replaces a conversation's snapshot and raises its change.</summary>
    public void Publish(ConversationSnapshot snapshot)
    {
        Replace(snapshot);
        Changed?.Invoke(snapshot.Id);
    }

    /// <summary>Replaces a conversation's snapshot without raising its change, as if the event were still on its way.</summary>
    public void Replace(ConversationSnapshot snapshot)
    {
        if (!_snapshots.ContainsKey(snapshot.Id))
        {
            _order.Add(snapshot.Id);
        }

        _snapshots[snapshot.Id] = snapshot;
    }

    /// <summary>Sets the total and raises a change, as any turn's progress does.</summary>
    public void PublishTotal(ConversationCost total)
    {
        TotalCost = total;
        Changed?.Invoke(Guid.NewGuid());
    }

    public static ConversationSnapshot Idle(Guid id) => new()
    {
        Id = id,
        Title = "New conversation",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Settings = AnswerSettings.Default,
        SettingsLocked = false,
        Corpus = null,
        Messages = [],
        Answers = [],
        InProgress = null,
        LastOutcome = null,
        ReturnedQuestion = null,
        Cost = ConversationCost.Zero,
        LastRequestTokens = null,
    };

    public static ConversationSnapshot Finished(Guid id, TurnOutcome outcome) => Idle(id) with { LastOutcome = outcome };

    public static ConversationSnapshot Answering(Guid id, bool stopping = false) => Idle(id) with
    {
        SettingsLocked = true,
        InProgress = new InProgressAnswer
        {
            Question = "What are the sump requirements?",
            Settings = AnswerSettings.Default,
            AskedAt = DateTimeOffset.UnixEpoch,
            Segments = [],
            Thinking = "",
            HasRedactedThinking = false,
            IsStopping = stopping,
        },
    };
}
