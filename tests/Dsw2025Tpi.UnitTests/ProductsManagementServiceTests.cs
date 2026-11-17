using System.Linq.Expressions;
using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Application.Exceptions;
using Dsw2025Tpi.Application.Services;
using Dsw2025Tpi.Domain.Entities;
using Dsw2025Tpi.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Dsw2025Tpi.UnitTests;

public class ProductsManagementServiceTests
{
    // xUnit crea una instancia NUEVA de esta clase por cada test,
    // así que cada test arranca con un mock y un servicio limpios.
    private readonly Mock<IRepository> _repoMock;
    private readonly ProductsManagementService _service;

    public ProductsManagementServiceTests()
    {
        _repoMock = new Mock<IRepository>();
        _service = new ProductsManagementService(
            _repoMock.Object,
            NullLogger<ProductsManagementService>.Instance);
    }

    // ---------- Helpers ----------

    private static Product CrearProducto(string sku, string nombre, string codigoInterno, bool activo = true)
    {
        var producto = new Product(sku, nombre, 100m, 10, "Descripción", codigoInterno);
        producto.IsActive = activo;
        return producto;
    }

    // Le enseña al mock a comportarse como un repositorio real:
    // aplica el filtro (predicado) que le manda el servicio sobre la lista de prueba.
    private void ConfigurarGetFiltered(List<Product> productos)
    {
        _repoMock
            .Setup(r => r.GetFiltered<Product>(
                It.IsAny<Expression<Func<Product, bool>>>(),
                It.IsAny<string[]>()))
            .ReturnsAsync((Expression<Func<Product, bool>> predicado, string[] include) =>
                (IEnumerable<Product>?)productos.Where(predicado.Compile()).ToList());
    }

    // =====================================================
    //  CREACIÓN DE PRODUCTOS
    // =====================================================

    [Fact]
    public async Task AddProduct_WithValidData_ReturnsProductAndCallsRepositoryAdd()
    {
        // Arrange
        var request = new ProductModel.Request("SKU-001", "INT-001", "Mouse Gamer", "Mouse RGB", 1500m, 10);

        // No existe ningún producto con ese SKU
        _repoMock
            .Setup(r => r.First<Product>(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()))
            .ReturnsAsync((Product?)null);

        _repoMock
            .Setup(r => r.Add(It.IsAny<Product>()))
            .ReturnsAsync((Product p) => p);

        // Act
        var result = await _service.AddProduct(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SKU-001", result.Sku);
        Assert.Equal("Mouse Gamer", result.Name);
        Assert.Equal(1500m, result.CurrentUnitPrice);
        Assert.Equal(10, result.StockQuantity);
        _repoMock.Verify(r => r.Add(It.IsAny<Product>()), Times.Once);
    }

    [Fact]
    public async Task AddProduct_WithDuplicateSku_ThrowsDuplicatedEntityException()
    {
        // Arrange
        var request = new ProductModel.Request("SKU-001", "INT-001", "Mouse Gamer", "Mouse RGB", 1500m, 10);
        var productoExistente = CrearProducto("SKU-001", "Otro producto", "INT-999");

        // El repositorio dice que YA existe un producto con ese SKU
        _repoMock
            .Setup(r => r.First<Product>(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()))
            .ReturnsAsync(productoExistente);

        // Act + Assert
        await Assert.ThrowsAsync<DuplicatedEntityException>(() => _service.AddProduct(request));

        // No se debe haber intentado guardar nada
        _repoMock.Verify(r => r.Add(It.IsAny<Product>()), Times.Never);
    }

    // Un mismo test que se ejecuta 3 veces con datos distintos
    [Theory]
    [InlineData(0, 100)]   // stock = 0
    [InlineData(-3, 100)]  // stock negativo
    [InlineData(5, -1)]    // precio negativo
    public async Task AddProduct_WithInvalidStockOrPrice_ThrowsInvalidDataException(int stock, int price)
    {
        // Arrange
        var request = new ProductModel.Request("SKU-001", "INT-001", "Mouse Gamer", "Mouse RGB", price, stock);

        // Act
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => _service.AddProduct(request));

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));

        // La validación ocurre ANTES de tocar el repositorio
        _repoMock.Verify(
            r => r.First<Product>(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()),
            Times.Never);
        _repoMock.Verify(r => r.Add(It.IsAny<Product>()), Times.Never);
    }

    // =====================================================
    //  LISTADO DE PRODUCTOS
    // =====================================================

    [Fact]
    public async Task GetProducts_WhenIncludeInactiveIsFalse_ReturnsOnlyActiveProducts()
    {
        // Arrange
        var productos = new List<Product>
        {
            CrearProducto("SKU-001", "Mouse",   "INT-001", activo: true),
            CrearProducto("SKU-002", "Teclado", "INT-002", activo: true),
            CrearProducto("SKU-003", "Monitor", "INT-003", activo: false)
        };
        ConfigurarGetFiltered(productos);
        var filter = new ProductModel.FilterProduct(null, null, null, null);

        // Act
        var result = await _service.GetProducts(filter, includeInactive: false);

        // Assert
        Assert.Equal(2, result.ProductItems.Count);
        Assert.All(result.ProductItems, p => Assert.True(p.IsActive));
        Assert.DoesNotContain(result.ProductItems, p => p.Sku == "SKU-003");
    }

    [Fact]
    public async Task GetProducts_WithPaginationFilter_AppliesSkipAndTakeCorrectly()
    {
        // Arrange: 15 productos "Producto 01" ... "Producto 15"
        var productos = Enumerable.Range(1, 15)
            .Select(i => CrearProducto($"SKU-{i:00}", $"Producto {i:00}", $"INT-{i:00}"))
            .ToList();
        ConfigurarGetFiltered(productos);
        var filter = new ProductModel.FilterProduct(null, null, PageNumber: 2, PageSize: 5);

        // Act
        var result = await _service.GetProducts(filter);

        // Assert
        Assert.Equal(5, result.ProductItems.Count);
        Assert.Equal(15, result.Total);

        // La página 2 (de a 5, ordenados por nombre) son del 06 al 10
        var nombres = result.ProductItems.Select(p => p.Name).ToList();
        Assert.Equal(
            new[] { "Producto 06", "Producto 07", "Producto 08", "Producto 09", "Producto 10" },
            nombres);
    }

    // Busca por nombre, por SKU y por código interno, sin importar mayúsculas
    [Theory]
    [InlineData("mouse")]     // nombre (en minúscula, el producto es "Mouse Gamer")
    [InlineData("sku-100")]   // SKU
    [InlineData("int-100")]   // código interno
    public async Task GetProducts_WithSearchTerm_FiltersByNameSkuOrInternalCode(string termino)
    {
        // Arrange
        var productos = new List<Product>
        {
            CrearProducto("SKU-100", "Mouse Gamer",      "INT-100"),
            CrearProducto("SKU-200", "Teclado Mecánico", "INT-200"),
            CrearProducto("SKU-300", "Monitor",          "INT-300")
        };
        ConfigurarGetFiltered(productos);
        var filter = new ProductModel.FilterProduct(null, termino, null, null);

        // Act
        var result = await _service.GetProducts(filter);

        // Assert
        var unico = Assert.Single(result.ProductItems);
        Assert.Equal("Mouse Gamer", unico.Name);
    }
}
