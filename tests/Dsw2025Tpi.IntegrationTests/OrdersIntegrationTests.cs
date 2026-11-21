using System.Net;
using System.Net.Http.Json;
using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Domain.Entities;
using Dsw2025Tpi.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dsw2025Tpi.IntegrationTests;

[Collection("IntegrationTests")]
public class OrdersIntegrationTests : ApiTestBase
{
    public OrdersIntegrationTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task CreateOrder_WhenUnauthenticated_ReturnsUnauthorized()
    {
        // Arrange: orden con formato válido, pero sin token
        var request = new OrderRequest(
            Guid.NewGuid(),
            "Av. Siempre Viva 742",
            "Av. Siempre Viva 742",
            null,
            new List<OrderItemRequest> { new OrderItemRequest(Guid.NewGuid(), 1) });

        // Act
        var response = await CreateAnonymousClient().PostAsJsonAsync("/api/orders", request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_WhenAuthenticatedWithValidData_ReturnsCreatedAndPersistsOrderAndStockInSqlite()
    {
        // Arrange: cliente y producto (stock 10, precio 250,50) sembrados en SQLite
        var customer = SeedCustomer();
        var product = SeedProduct(stock: 10, price: 250.50m);
        var client = CreateClientWithRole("User", customer.Id.ToString());

        var request = new OrderRequest(
            customer.Id,
            "Av. Siempre Viva 742",
            "Av. Siempre Viva 742",
            "Tocar timbre",
            new List<OrderItemRequest> { new OrderItemRequest(product.Id, 3) });

        // Act
        var response = await client.PostAsJsonAsync("/api/orders", request);

        // Assert: respuesta HTTP 201 con header Location apuntando a la nueva orden
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains(created!.OrderId.ToString(), response.Headers.Location!.ToString());
        Assert.Equal(751.50m, created.TotalAmount); // 3 x 250,50
        Assert.Equal(OrderStatus.PENDING, created.Status);

        // Assert: la orden y su ítem están guardados en la tabla correspondiente
        var order = WithDb(db => db.Set<Order>()
            .AsNoTracking()
            .Include(o => o.OrderItems)
            .FirstOrDefault(o => o.Id == created.OrderId));
        Assert.NotNull(order);
        Assert.Equal(customer.Id, order!.CustomerId);
        Assert.Equal(751.50m, order.TotalAmount);

        var item = Assert.Single(order.OrderItems!);
        Assert.Equal(product.Id, item.ProductId);
        Assert.Equal(3, item.Quantity);

        // Assert: el stock del producto bajó de 10 a 7 en la base
        var stockAfter = WithDb(db => db.Set<Product>().AsNoTracking().First(p => p.Id == product.Id).StockQuantity);
        Assert.Equal(7, stockAfter);
    }

    [Fact]
    public async Task CreateOrder_WhenStockIsInsufficient_ReturnsBadRequestWithErrorMessage()
    {
        // Arrange: producto con stock 2 y una orden que pide 5
        var customer = SeedCustomer();
        var product = SeedProduct(name: $"Producto Escaso {UniqueId()}", stock: 2);
        var client = CreateClientWithRole("User", customer.Id.ToString());

        var request = new OrderRequest(
            customer.Id,
            "Av. Siempre Viva 742",
            "Av. Siempre Viva 742",
            null,
            new List<OrderItemRequest> { new OrderItemRequest(product.Id, 5) });

        // Act
        var response = await client.PostAsJsonAsync("/api/orders", request);

        // Assert: 400 con un JSON que explica la falta de stock y nombra al producto
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errorText = await ReadErrorTextAsync(response);
        Assert.Contains("stock", errorText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(product.Name!, errorText);

        // Assert: no se guardó ninguna orden y el stock no cambió
        var ordersCount = WithDb(db => db.Set<Order>().Count(o => o.CustomerId == customer.Id));
        Assert.Equal(0, ordersCount);

        var stockAfter = WithDb(db => db.Set<Product>().AsNoTracking().First(p => p.Id == product.Id).StockQuantity);
        Assert.Equal(2, stockAfter);
    }
}
