namespace Tikai.Core.Events;

/// <summary>
/// A change notification. <see cref="Id"/> names the changed row; listeners re-read it.
/// Id is null after the listener reconnected: events may have been missed, so re-read all state for the topic.
/// </summary>
public sealed record BusEvent(string Topic, Guid? Id)
{
    public bool IsResync => Id is null;
}
