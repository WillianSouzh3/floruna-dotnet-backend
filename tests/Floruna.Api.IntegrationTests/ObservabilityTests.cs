using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Telemetry;
using Floruna.Infrastructure.Mail;
using Floruna.Infrastructure.Outbox;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Traces ponta a ponta (inclusive pela outbox), métricas de negócio e saúde da fila.</summary>
[Collection(ApiCollection.Name)]
public sealed class ObservabilityTests(FlorunaApiFactory factory)
{
    [Fact]
    public async Task Async_work_from_the_outbox_continues_the_checkout_trace()
    {
        using var api = factory.WithEmail(new CapturingEmailTransport());
        var finished = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FlorunaTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = finished.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var store = await StoreAsync();
        var product = await store.ProductAsync();

        var checkout = await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product));
        checkout.StatusCode.ShouldBe(HttpStatusCode.Created);
        var traceId = checkout.Headers.GetValues("X-Trace-Id").Single(); // o código que o suporte recebe do cliente

        var processor = api.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessOnceAsync(CancellationToken.None) > 0; i++)
        {
        }

        // O span do comando e o da entrega do e-mail (no "worker") estão no MESMO trace do checkout.
        finished.ShouldContain(a => a.DisplayName == "PlaceOrderCommand" && a.TraceId.ToString() == traceId);
        var delivery = finished.First(a => a.DisplayName == "outbox OrderPlaced" && a.TraceId.ToString() == traceId);
        delivery.Kind.ShouldBe(ActivityKind.Consumer);
        delivery.GetTagItem("messaging.system").ShouldBe("outbox");
    }

    [Fact]
    public async Task Business_metrics_count_orders_by_origin_and_request_outcomes()
    {
        var measurements = new ConcurrentBag<(string Instrument, long Value, string? Tag)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == FlorunaTelemetry.Name)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? tag = null;
            foreach (var t in tags)
            {
                if (t.Key is "origin" or "outcome")
                {
                    tag = $"{t.Key}={t.Value}";
                }
            }

            measurements.Add((instrument.Name, value, tag));
        });
        listener.Start();
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 1);

        (await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product))).StatusCode.ShouldBe(HttpStatusCode.Conflict); // sem estoque

        measurements.ShouldContain(m => m.Instrument == "floruna.orders.placed" && m.Tag == "origin=online");
        measurements.ShouldContain(m => m.Instrument == "floruna.orders.value" && m.Value == 10_000);
        measurements.ShouldContain(m => m.Instrument == "floruna.requests" && m.Tag == "outcome=conflict"); // a recusa por estoque também é medida
    }

    [Fact]
    public async Task Outbox_health_reports_age_of_the_oldest_pending_and_dead_letters()
    {
        var store = await StoreAsync();
        await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(await store.ProductAsync()));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
            var message = await db.OutboxMessages.OrderByDescending(m => m.OccurredAt).FirstAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE outbox_messages SET occurred_at = now() - interval '10 minutes' WHERE id = {message.Id}");
        }

        await factory.Services.GetRequiredService<OutboxProcessor>().SampleHealthAsync(CancellationToken.None);

        // Cada variação do WebApplicationFactory é um host com o SEU medidor: lê só o deste host
        // (medidores criados pelo IMeterFactory levam a fábrica como escopo).
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        var gauges = new Dictionary<string, double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == FlorunaTelemetry.Name && ReferenceEquals(instrument.Meter.Scope, meterFactory)
                    && instrument.Name.StartsWith("floruna.outbox.", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => gauges[instrument.Name] = value);
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => gauges[instrument.Name] = value);
        listener.Start();
        listener.RecordObservableInstruments();

        gauges["floruna.outbox.oldest_pending_age"].ShouldBeGreaterThanOrEqualTo(600); // RNF-04 mensurável: alarme se passar de 60 s
        gauges["floruna.outbox.pending"].ShouldBeGreaterThanOrEqualTo(1);
        gauges.ShouldContainKey("floruna.outbox.dead_letters");
    }

    [Fact]
    public async Task Every_response_carries_request_and_trace_ids()
    {
        var store = await StoreAsync();

        var response = await store.Shopper.GetAsync("/api/v1/products");

        response.Headers.GetValues("X-Request-ID").Single().ShouldNotBeNullOrEmpty();
        response.Headers.GetValues("X-Trace-Id").Single().Length.ShouldBe(32); // W3C trace-id
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private static object Checkout(string productId) => new
    {
        customerName = "Ana Cliente",
        customerPhone = "(11) 99999-0000",
        customerEmail = "ana@trace.com.br",
        items = new[] { new { productId, quantity = 1 } },
        deliveryMethod = "retirada",
    };

    private async Task<Store> StoreAsync()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return new Store(factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)), factory.ForStore(slug));
    }

    private sealed record Store(HttpClient Admin, HttpClient Shopper)
    {
        public async Task<string> ProductAsync(int stock = 5)
        {
            var response = await Admin.PostAsJsonAsync("/api/v1/products", new
            {
                name = $"Flor {Guid.NewGuid():N}"[..16],
                shortDescription = "Uma descrição curta",
                description = "Uma descrição longa o suficiente.",
                category = "buques",
                price = 10_000,
                images = new[] { "https://cdn.test/1.webp" },
                stock,
                isActive = true,
                isFeatured = false,
            });
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("product").GetProperty("id").GetString()!;
        }
    }
}
