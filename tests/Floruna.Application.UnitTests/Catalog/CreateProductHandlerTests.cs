using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Domain.Catalog;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Floruna.Application.UnitTests.Catalog;

public sealed class CreateProductHandlerTests
{
    private readonly TenantContext _tenant = new();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private ProductCommandHandlers Handler(TenantPlan plan)
    {
        _tenant.Set(new ResolvedTenant(TenantId.New(), "loja", TenantStatus.Active, plan));
        return new ProductCommandHandlers(_tenant, _catalog, Substitute.For<Floruna.Domain.Inventory.IInventoryRepository>(), Substitute.For<ICatalogQueries>(), Substitute.For<ICatalogCache>(),
            _unitOfWork, new FakeTimeProvider());
    }

    private static CreateProductCommand Command(int images = 1) => new(new ProductInput(
        "Buquê", null, "Descrição curta ok", "Descrição longa suficiente", "buques", null, 10_000,
        Optional<long?>.Absent, Optional<long?>.Absent, Enumerable.Repeat("https://cdn/x.webp", images).ToList(),
        5, null, true, false, null, null, null));

    [Fact]
    public async Task Free_plan_stops_at_25_products()
    {
        _catalog.CountProductsAsync(Arg.Any<CancellationToken>()).Returns(25);

        var result = await Handler(TenantPlan.Free).Handle(Command(), default);

        result.Error.ShouldBe(CatalogErrors.ProductLimit(25));
        _catalog.DidNotReceive().Add(Arg.Any<Product>());
    }

    [Fact]
    public async Task Image_limit_depends_on_the_plan()
    {
        var result = await Handler(TenantPlan.Free).Handle(Command(images: 4), default);

        result.Error.ShouldBe(CatalogErrors.ImageLimit(3));
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }
}
