using Hive.Notifications;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class NotificationStateTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static NotificationRecipientState CreateState(
        NotificationType type,
        DateTimeOffset? cancelledAtUtc = null) =>
        new(
            NotificationId: Guid.NewGuid(),
            RecipientKey: "S-1-5-21-1001",
            Type: type,
            DeliveredAtUtc: BaseTime,
            ReadAtUtc: null,
            AcknowledgedAtUtc: null,
            CompletedAtUtc: null,
            CancelledAtUtc: cancelledAtUtc,
            Version: 1);

    [Theory]
    [InlineData(NotificationType.SoftwareUpdate)]
    [InlineData(NotificationType.RequiredAction)]
    [InlineData(NotificationType.Maintenance)]
    [InlineData(NotificationType.CriticalCondition)]
    public void ActionableTypes_RemainUnresolvedAfterAcknowledge_AndClearOnComplete(NotificationType type)
    {
        var initial = CreateState(type);
        Assert.True(initial.IsActionableUnresolved());

        var read = initial.MarkRead(BaseTime.AddMinutes(1));
        Assert.True(read.IsActionableUnresolved());

        var acked = read.MarkAcknowledged(BaseTime.AddMinutes(2));
        Assert.True(acked.IsActionableUnresolved());
        Assert.NotNull(acked.AcknowledgedAtUtc);
        Assert.Null(acked.CompletedAtUtc);

        var completed = acked.MarkCompleted(BaseTime.AddMinutes(3));
        Assert.False(completed.IsActionableUnresolved());
        Assert.Equal(4, completed.Version);
    }

    [Fact]
    public void Announcement_ClearsOnAcknowledge_NotOnReadAlone()
    {
        var initial = CreateState(NotificationType.Announcement);
        Assert.True(initial.IsActionableUnresolved());

        var read = initial.MarkRead(BaseTime.AddMinutes(1));
        Assert.True(read.IsActionableUnresolved());

        var acked = read.MarkAcknowledged(BaseTime.AddMinutes(2));
        Assert.False(acked.IsActionableUnresolved());
    }

    [Fact]
    public void Informational_ClearsOnRead()
    {
        var initial = CreateState(NotificationType.Informational);
        Assert.True(initial.IsActionableUnresolved());

        var read = initial.MarkRead(BaseTime.AddMinutes(1));
        Assert.False(read.IsActionableUnresolved());
    }

    [Theory]
    [InlineData(NotificationType.Informational)]
    [InlineData(NotificationType.Announcement)]
    [InlineData(NotificationType.RequiredAction)]
    [InlineData(NotificationType.SoftwareUpdate)]
    [InlineData(NotificationType.Maintenance)]
    [InlineData(NotificationType.CriticalCondition)]
    public void CancelledNotification_IsNeverUnresolved(NotificationType type)
    {
        var cancelled = CreateState(type, cancelledAtUtc: BaseTime.AddMinutes(5));
        Assert.False(cancelled.IsActionableUnresolved());
    }

    [Fact]
    public void StateTransitions_PreserveFirstTimestamp()
    {
        var firstTime = BaseTime.AddMinutes(1);
        var secondTime = BaseTime.AddMinutes(10);

        var state = CreateState(NotificationType.SoftwareUpdate)
            .MarkAcknowledged(firstTime)
            .MarkAcknowledged(secondTime);

        Assert.Equal(firstTime, state.AcknowledgedAtUtc);
        Assert.Equal(3, state.Version);
    }
}
