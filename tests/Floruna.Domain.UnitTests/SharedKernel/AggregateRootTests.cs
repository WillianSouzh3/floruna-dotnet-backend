using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.SharedKernel;

public sealed class AggregateRootTests
{
    private sealed record SampleCreated(DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

    private sealed class Sample(Guid id) : AggregateRoot<Guid>(id)
    {
        public static Sample Create(Guid id, DateTimeOffset now)
        {
            var sample = new Sample(id);
            sample.Raise(new SampleCreated(now));
            return sample;
        }
    }

    private sealed class Other(Guid id) : AggregateRoot<Guid>(id);

    [Fact]
    public void Raised_events_are_recorded_until_cleared()
    {
        var sample = Sample.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        sample.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<SampleCreated>();

        sample.ClearDomainEvents();
        sample.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Each_event_gets_a_unique_id()
    {
        var first = new SampleCreated(DateTimeOffset.UnixEpoch);
        var second = new SampleCreated(DateTimeOffset.UnixEpoch);

        first.EventId.ShouldNotBe(second.EventId);
    }

    [Fact]
    public void Entities_are_equal_by_identity_and_type()
    {
        var id = Guid.NewGuid();

        new Sample(id).ShouldBe(new Sample(id));
        (new Sample(id) == new Sample(Guid.NewGuid())).ShouldBeFalse();
        new Sample(id).Equals(new Other(id)).ShouldBeFalse();
    }
}
