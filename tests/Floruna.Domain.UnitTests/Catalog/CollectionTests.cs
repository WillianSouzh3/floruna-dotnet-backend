using Floruna.Domain.Catalog;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Catalog;

public sealed class CollectionTests
{
    private static readonly DateTimeOffset MothersDay = new(2027, 5, 9, 12, 0, 0, TimeSpan.Zero);

    private static Collection MothersDayCollection(bool active = true) => Collection.Create(
        TenantId.New(), "Dia das Mães", null, null, active, MothersDay.AddDays(-8), MothersDay.AddDays(1)).Value;

    [Fact]
    public void State_follows_the_date_window()
    {
        var collection = MothersDayCollection();

        collection.StateAt(MothersDay.AddDays(-10)).ShouldBe(CollectionState.Scheduled);
        collection.StateAt(MothersDay).ShouldBe(CollectionState.Active);
        collection.StateAt(MothersDay.AddDays(2)).ShouldBe(CollectionState.Ended);
        collection.IsVisibleAt(MothersDay).ShouldBeTrue();
        collection.IsVisibleAt(MothersDay.AddDays(2)).ShouldBeFalse();
    }

    [Fact]
    public void Inactive_collection_is_never_visible()
    {
        MothersDayCollection(active: false).StateAt(MothersDay).ShouldBe(CollectionState.Inactive);
    }

    [Fact]
    public void End_must_be_after_start()
    {
        Collection.Create(TenantId.New(), "Natal", null, null, true, MothersDay, MothersDay).Error.ShouldBe(CatalogErrors.CollectionWindowInvalid);
    }

    [Fact]
    public void Products_keep_the_curated_order_and_cannot_repeat()
    {
        var collection = MothersDayCollection();
        var (a, b) = (ProductId.New(), ProductId.New());

        collection.ReplaceProducts([b, a]).IsSuccess.ShouldBeTrue();
        collection.Items.OrderBy(i => i.Position).Select(i => i.ProductId).ShouldBe([b, a]);

        collection.ReplaceProducts([a, a]).Error.ShouldBe(CatalogErrors.DuplicateCollectionProducts);
    }
}
