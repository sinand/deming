namespace Deming.Data.Models;

/// <summary>
/// Row of table <c>message</c>: one line in a channel between agents. The channel is the task
/// (<see cref="TaskId"/>), or the project when it is null. A message is data, not an instruction.
/// </summary>
public sealed class Message
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long? TaskId { get; set; }

    /// <summary>The writing agent's label (lowercase ASCII).</summary>
    public string Sender { get; set; } = "";
    public string? SenderSessionId { get; set; }

    /// <summary>The addressed agent's label; null = everyone.</summary>
    public string? Recipient { get; set; }

    /// <summary>The message this one answers (always in the same channel).</summary>
    public long? ReplyToId { get; set; }
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>What a write hands back: the message without its body (the writer already has it).</summary>
public sealed class MessagePosted
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long? TaskId { get; set; }
    public string Sender { get; set; } = "";
    public string? SenderSessionId { get; set; }
    public string? Recipient { get; set; }
    public long? ReplyToId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One read of a channel, oldest first. <see cref="LastMessageId"/> is the reader's new cursor
/// (the cursor it sent when nothing came); <see cref="HasMore"/> says more is waiting after it.
/// </summary>
public sealed class MessagePage
{
    public required IReadOnlyList<Message> Messages { get; init; }
    public long LastMessageId { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>A task channel at done time (Sql/Message/TaskStats.sql).</summary>
public sealed class MessageTaskStats
{
    public long MessageCount { get; set; }

    /// <summary>
    /// An estimate: a message that is addressed to an agent or holds a '?', is not itself a reply,
    /// and has no reply. Questions are not marked, so this counts what looks like one.
    /// </summary>
    public long UnansweredQuestionCount { get; set; }
}

/// <summary>Caps and limits of the message channel.</summary>
public static class MessageLimits
{
    /// <summary>Body target 2000 characters; the database accepts 10 % more.</summary>
    public const int BodyTarget = 2000;
    public const int BodyMax = 2200;

    /// <summary>Agent labels: lowercase ASCII letters, digits, '.', '_', '-'.</summary>
    public const int LabelMax = 64;

    /// <summary>Seconds a read may wait for a message. Over the limit is refused, not shortened:
    /// a wait cut short would say "no message" too early.</summary>
    public const int WaitMaxSeconds = 60;

    public const int TopDefault = 20;
    public const int TopMax = 100;
}
