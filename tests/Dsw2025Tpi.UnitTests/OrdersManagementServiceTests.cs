using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Application.Exceptions;
using Dsw2025Tpi.Application.Services;
using Dsw2025Tpi.Domain.Entities;
using Dsw2025Tpi.Domain.Enums;
using Dsw2025Tpi.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Dsw2025Tpi.UnitTests;

public class OrdersManagementServiceTests
{
    // xUnit crea una instancia nueva de esta clase por cada test:
    // cada test arranca con un mock y un servicio limpios.
    private readonly Mock<IRepository> _repoMock;
    private readonly OrdersManagementService _service;

    public OrdersManagementServiceTests()
    {
        _repoMock = new Mock<IRepository>();
        _service = new OrdersManagementService(
            _repoMock.Object,
            NullLogger<OrdersManagementService>.Instance);
    }

    // ---------- Helpers ----------

    private static Customer CrearCliente() =>
        new Customer { Name = "Juan Pérez", Email = "juan@mail.com", PhoneNumber = "381-1234567" };

    private static Product CrearProducto(string nombre, decimal precio, int stock) =>
        new Product($"SKU-{nombre}", nombre, precio, stock, "Descripción", $"INT-{nombre}");

    // Le dice al mock: "si te piden este cliente, devolvelo"
    private void ConfigurarCliente(Customer cliente)
    {
        _repoMock
            .Setup(r => r.GetById<Customer>(cliente.Id, It.IsAny<string[]>()))
            .ReturnsAsync(cliente);
    }

    // Le dice al mock: "si te piden este producto, devolvelo"
    private void ConfigurarProducto(Product producto)
    {
        _repoMock
            .Setup(r => r.GetById<Product>(producto.Id, It.IsAny<string[]>()))
            .ReturnsAsync(producto);
    }

    // Update y Add devuelven la misma entidad que reciben (como haría el repo real)
    private void ConfigurarGuardado()
    {
        _repoMock.Setup(r => r.Update(It.IsAny<Product>())).ReturnsAsync((Product p) => p);
        _repoMock.Setup(r => r.Add(It.IsAny<Order>())).ReturnsAsync((Order o) => o);
    }

    private static OrderRequest CrearRequest(Guid clienteId, params OrderItemRequest[] items) =>
        new OrderRequest(clienteId, "Av. Siempre Viva 742", "Av. Siempre Viva 742", "Tocar timbre", items.ToList());

    // =====================================================
    //  CREACIÓN DE ORDEN
    // =====================================================

    [Fact]
    public async Task CreateOrder_WithValidItemsAndStock_CalculatesTotalAndDecrementsProductStock()
    {
        // Arrange
        var cliente = CrearCliente();
        var mouse = CrearProducto("Mouse", 100m, 10);      // stock 10, precio 100
        var teclado = CrearProducto("Teclado", 250.50m, 5); // stock 5, precio 250.50

        ConfigurarCliente(cliente);
        ConfigurarProducto(mouse);
        ConfigurarProducto(teclado);
        ConfigurarGuardado();

        // 3 mouses (300.00) + 2 teclados (501.00) = 801.00
        var request = CrearRequest(
            cliente.Id,
            new OrderItemRequest(mouse.Id, 3),
            new OrderItemRequest(teclado.Id, 2));

        // Act
        var result = await _service.CreateOrder(request);

        // Assert: resultado devuelto
        Assert.Equal(801.00m, result.TotalAmount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(OrderStatus.PENDING, result.Status);
        Assert.Equal("Juan Pérez", result.ClientName);

        // Assert: se descontó el stock de los productos
        Assert.Equal(7, mouse.StockQuantity);   // 10 - 3
        Assert.Equal(3, teclado.StockQuantity); // 5 - 2

        // Assert: se actualizó cada producto con su stock nuevo
        _repoMock.Verify(
            r => r.Update(It.Is<Product>(p => p.Id == mouse.Id && p.StockQuantity == 7)),
            Times.Once);
        _repoMock.Verify(
            r => r.Update(It.Is<Product>(p => p.Id == teclado.Id && p.StockQuantity == 3)),
            Times.Once);

        // Assert: se guardó la orden una sola vez, con el total y el cliente correctos
        _repoMock.Verify(
            r => r.Add(It.Is<Order>(o => o.TotalAmount == 801.00m && o.CustomerId == cliente.Id)),
            Times.Once);
    }

    [Fact]
    public async Task CreateOrder_WhenProductStockIsInsufficient_ThrowsInsufficientStockException()
    {
        // Arrange: producto con stock 2, pero se piden 5
        var cliente = CrearCliente();
        var mouse = CrearProducto("Mouse", 100m, 2);

        ConfigurarCliente(cliente);
        ConfigurarProducto(mouse);
        ConfigurarGuardado();

        var request = CrearRequest(cliente.Id, new OrderItemRequest(mouse.Id, 5));

        // Act
        var ex = await Assert.ThrowsAsync<InsufficientStockException>(() => _service.CreateOrder(request));

        // Assert: el mensaje nombra al producto
        Assert.Contains("Mouse", ex.Message);

        // No se guardó la orden ni se tocó el producto
        _repoMock.Verify(r => r.Add(It.IsAny<Order>()), Times.Never);
        _repoMock.Verify(r => r.Update(It.IsAny<Product>()), Times.Never);
        Assert.Equal(2, mouse.StockQuantity);
    }

    [Fact]
    public async Task CreateOrder_WhenCustomerDoesNotExist_ThrowsCustomerNotFoundException()
    {
        // Arrange: el repositorio no encuentra al cliente (devuelve null)
        var clienteInexistenteId = Guid.NewGuid();
        var productoId = Guid.NewGuid();

        _repoMock
            .Setup(r => r.GetById<Customer>(It.IsAny<Guid>(), It.IsAny<string[]>()))
            .ReturnsAsync((Customer?)null);

        // La orden tiene un ítem válido, así la excepción es por el cliente y no por ítems vacíos
        var request = CrearRequest(clienteInexistenteId, new OrderItemRequest(productoId, 1));

        // Act
        var ex = await Assert.ThrowsAsync<CustomerNotFoundException>(() => _service.CreateOrder(request));

        // Assert: el mensaje incluye el id buscado
        Assert.Contains(clienteInexistenteId.ToString(), ex.Message);

        // Falla de inmediato: no se consultaron productos ni se modificó nada
        _repoMock.Verify(r => r.GetById<Product>(It.IsAny<Guid>(), It.IsAny<string[]>()), Times.Never);
        _repoMock.Verify(r => r.Update(It.IsAny<Product>()), Times.Never);
        _repoMock.Verify(r => r.Add(It.IsAny<Order>()), Times.Never);
    }
}
