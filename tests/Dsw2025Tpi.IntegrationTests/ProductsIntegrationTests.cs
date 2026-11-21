using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dsw2025Tpi.IntegrationTests;

[Collection("IntegrationTests")]
public class ProductsIntegrationTests : ApiTestBase
{
    public ProductsIntegrationTests(CustomWebApplicationFactory factory) : base(factory) { }

    // =====================================================
    //  CREACIÓN DE PRODUCTOS (Admin)
    // =====================================================

    [Fact]
    public async Task AddProduct_WhenUnauthenticatedOrNotAdmin_ReturnsUnauthorizedOrForbidden()
    {
        // Arrange
        var sku = $"SKU-{UniqueId()}";
        var request = new ProductModel.Request(sku, $"INT-{UniqueId()}", "Producto Seguridad", "Descripción", 100m, 5);

        // Act + Assert 1: sin token -> 401 Unauthorized
        var anonymousResponse = await CreateAnonymousClient().PostAsJsonAsync("/api/products", request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        // Act + Assert 2: token con rol "User" (no Admin) -> 403 Forbidden
        var userResponse = await CreateClientWithRole("User").PostAsJsonAsync("/api/products", request);
        Assert.Equal(HttpStatusCode.Forbidden, userResponse.StatusCode);

        // Ninguno de los dos intentos debe haber guardado el producto
        var saved = WithDb(db => db.Set<Product>().FirstOrDefault(p => p.Sku == sku));
        Assert.Null(saved);
    }

    [Fact]
    public async Task AddProduct_WhenAuthenticatedAsAdmin_PersistsInSqliteAndReturnsCreated()
    {
        // Arrange
        var sku = $"SKU-{UniqueId()}";
        var request = new ProductModel.Request(sku, $"INT-{UniqueId()}", "Mouse Gamer", "Mouse RGB 7200 DPI", 1500m, 10);
        var client = CreateClientWithRole("Admin");

        // Act
        var response = await client.PostAsJsonAsync("/api/products", request);

        // Assert: respuesta HTTP
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var created = await response.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>(JsonOptions);
        Assert.NotNull(created);

        // Assert: la fila existe de verdad en la base SQLite, con los valores enviados
        var saved = WithDb(db => db.Set<Product>().AsNoTracking().FirstOrDefault(p => p.Sku == sku));
        Assert.NotNull(saved);
        Assert.Equal(created!.Id, saved!.Id);
        Assert.Equal("Mouse Gamer", saved.Name);
        Assert.Equal(1500m, saved.CurrentUnitPrice);
        Assert.Equal(10, saved.StockQuantity);
        Assert.True(saved.IsActive);
    }

    [Fact]
    public async Task AddProduct_WhenSkuAlreadyExistsInDatabase_ReturnsConflictStatusCode()
    {
        // Arrange: producto ya guardado en la base con un SKU
        var existing = SeedProduct();
        var request = new ProductModel.Request(existing.Sku!, $"INT-{UniqueId()}", "Otro producto", "Mismo SKU", 200m, 5);
        var client = CreateClientWithRole("Admin");

        // Act
        var response = await client.PostAsJsonAsync("/api/products", request);

        // Assert: no es un 500. El middleware traduce DuplicatedEntityException a 400
        // (el enunciado acepta 409 Conflict o 400 Bad Request).
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"Se esperaba 400 o 409 y se recibió {(int)response.StatusCode}");

        // El JSON de error menciona el SKU duplicado
        var errorText = await ReadErrorTextAsync(response);
        Assert.Contains(existing.Sku!, errorText);

        // Sigue habiendo un solo producto con ese SKU
        var count = WithDb(db => db.Set<Product>().Count(p => p.Sku == existing.Sku));
        Assert.Equal(1, count);
    }

    // =====================================================
    //  LISTADO DE PRODUCTOS
    // =====================================================

    [Fact]
    public async Task GetProducts_PublicEndpoint_ReturnsOkWithPaginatedJsonStructure()
    {
        // Arrange: nos aseguramos de que haya al menos un producto activo (si no, la API responde 204)
        SeedProduct();

        // Act: petición anónima
        var response = await CreateAnonymousClient().GetAsync("/api/products");

        // Assert: 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert: contrato JSON (nombres de las propiedades)
        var raw = await response.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(raw))
        {
            Assert.True(doc.RootElement.TryGetProperty("productItems", out _));
            Assert.True(doc.RootElement.TryGetProperty("total", out _));
        }

        // Assert: deserializa a la estructura de paginación
        var page = JsonSerializer.Deserialize<ProductModel.ResponsePagination>(raw, JsonOptions);
        Assert.NotNull(page);
        Assert.NotEmpty(page!.ProductItems);
        Assert.True(page.Total >= page.ProductItems.Count);
        Assert.All(page.ProductItems, p => Assert.True(p.IsActive));
    }

    [Fact]
    public async Task GetProducts_WithQueryStringFilters_ReturnsFilteredResultsFromSqlite()
    {
        // Arrange: 3 productos; solo 2 llevan el término "Gamer-{tag}" en el nombre
        var tag = UniqueId();
        SeedProduct(name: $"Gamer-{tag} Mouse");
        SeedProduct(name: $"Gamer-{tag} Teclado");
        SeedProduct(name: $"Monitor {tag}");
        var client = CreateAnonymousClient();

        // Act: el término va en minúsculas, el nombre guardado tiene mayúscula
        var page = await GetPageAsync(client, $"/api/products?search=gamer-{tag}&pageNumber=1&pageSize=10");

        // Assert: solo vienen los 2 que coinciden
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.ProductItems.Count);
        Assert.All(page.ProductItems, p => Assert.Contains($"Gamer-{tag}", p.Name));
        Assert.DoesNotContain(page.ProductItems, p => p.Name!.StartsWith("Monitor"));

        // Act + Assert: pageSize y pageNumber también se leen de la query string
        var secondPage = await GetPageAsync(client, $"/api/products?search=gamer-{tag}&pageNumber=2&pageSize=1");
        Assert.Single(secondPage.ProductItems);
        Assert.Equal(2, secondPage.Total);
    }

    [Fact]
    public async Task GetAuthProducts_AdminEndpoint_WhenCalledByAdmin_IncludesInactiveProducts()
    {
        // Arrange: un producto inactivo y uno activo, ambos con el mismo "tag" en el nombre
        var tag = UniqueId();
        var inactive = SeedProduct(name: $"Inactivo-{tag}", isActive: false);
        var active = SeedProduct(name: $"Activo-{tag}", isActive: true);

        // Act: el admin consulta el endpoint administrativo
        var adminPage = await GetPageAsync(CreateClientWithRole("Admin"), $"/api/products/admin?search={tag}");

        // Assert: aparecen los dos, incluido el inactivo
        Assert.Equal(2, adminPage.Total);
        Assert.Contains(adminPage.ProductItems, p => p.Sku == inactive.Sku && !p.IsActive);
        Assert.Contains(adminPage.ProductItems, p => p.Sku == active.Sku && p.IsActive);

        // Contraste: el endpoint público, con la misma búsqueda, solo muestra el activo
        var publicPage = await GetPageAsync(CreateAnonymousClient(), $"/api/products?search={tag}");
        Assert.Single(publicPage.ProductItems);
        Assert.DoesNotContain(publicPage.ProductItems, p => p.Sku == inactive.Sku);
    }

    // ---------- Helper ----------

    private async Task<ProductModel.ResponsePagination> GetPageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<ProductModel.ResponsePagination>(JsonOptions);
        Assert.NotNull(page);
        return page!;
    }
}
