using Floruna.Domain.Workshops;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Workshops;

public sealed class WorkshopTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static WorkshopSpec Spec(int capacity = 10, long price = 0, string? slug = null) =>
        new("Oficina de Arranjos de Natal", slug, "Aprenda a montar um arranjo de mesa", "Uma tarde inteira com flores da estação e café.",
            "2026-12-12T14:00", "Loja Centro", "https://cdn.test/oficina.webp", price, capacity, true);

    [Fact]
    public void Slug_is_generated_from_the_title()
    {
        var workshop = Workshop.Create(TenantId.New(), Spec(), Now).Value;

        workshop.Slug.ShouldBe("oficina-de-arranjos-de-natal");
        workshop.Registered.ShouldBe(0);
        workshop.SeatsLeft.ShouldBe(10);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, -1)]
    public void Capacity_must_be_positive_and_price_not_negative(int capacity, long price)
    {
        Workshop.Create(TenantId.New(), Spec(capacity, price), Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Capacity_cannot_drop_below_existing_registrations()
    {
        var workshop = Workshop.FromPersistenceForTests(TenantId.New(), Spec(10), registered: 7, Now);

        workshop.Update(Spec(capacity: 6), Now).Error.Message.ShouldContain("7 inscrições");
        workshop.Update(Spec(capacity: 7), Now).IsSuccess.ShouldBeTrue();
        workshop.SeatsLeft.ShouldBe(0);
    }

    [Fact]
    public void Workshop_with_registrations_cannot_be_deleted()
    {
        Workshop.FromPersistenceForTests(TenantId.New(), Spec(), registered: 1, Now).EnsureCanBeRemoved().IsFailure.ShouldBeTrue();
        Workshop.Create(TenantId.New(), Spec(), Now).Value.EnsureCanBeRemoved().IsSuccess.ShouldBeTrue();
    }
}
