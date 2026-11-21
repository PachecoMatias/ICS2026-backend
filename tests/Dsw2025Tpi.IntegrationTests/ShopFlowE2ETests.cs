using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Prueba End-to-End a nivel API (sin frontend): encadena varias peticiones HTTP reales
/// siguiendo el recorrido completo de un usuario, donde cada paso depende del anterior.
/// A diferencia de las pruebas de integración, acá NO se siembran productos ni órdenes
/// directamente en la base: todo el estado se construye usando la propia API.
/// </summary>
[Collection("IntegrationTests")]
[Trait("Category", "E2E")]
public class ShopFlowE2ETests : ApiTestBase
{
    private const string TestPassword = "E2eTest#2026";

    public ShopFlowE2ETests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task E2E_AdminCreatesProduct_CustomerRegistersAndBuysIt_StockIsUpdatedAndProtected()
    {
        var tag = UniqueId();
        var anonymous = CreateAnonymousClient();

        // ---------- 1. El administrador inicia sesión (login real) ----------
        var admin = await CreateAdminUserAsync(tag);
        var (adminClient, _) = await LoginAsync(admin.Username, TestPassword);

        // ---------- 2. El administrador crea un producto ----------
        var sku = $"SKU-{tag}";
        var productRequest = new ProductModel.Request(
            sku, $"INT-{tag}", $"E2E Notebook {tag}", "Notebook para el flujo E2E", 1200.50m, 5);

        var createProductResponse = await adminClient.PostAsJsonAsync("/api/products", productRequest);
        Assert.Equal(HttpStatusCode.Created, createProductResponse.StatusCode);

        var product = await createProductResponse.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>(JsonOptions);
        Assert.NotNull(product);

        // ---------- 3. Un visitante sin cuenta ve el producto en el listado público ----------
        var listing = await GetJsonAsync<ProductModel.ResponsePagination>(anonymous, $"/api/products?search={tag}");
        Assert.Equal(1, listing.Total);
        var listed = Assert.Single(listing.ProductItems);
        Assert.Equal(sku, listed.Sku);
        Assert.Equal(5, listed.StockQuantity);
        Assert.True(listed.IsActive);

        // ---------- 4. El cliente se registra e inicia sesión ----------
        var customerUsername = $"cliente-{tag}";
        var registerResponse = await anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            username = customerUsername,
            email = $"{customerUsername}@test.com",
            password = TestPassword
        });
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);

        var (customerClient, customerId) = await LoginAsync(customerUsername, TestPassword);
        Assert.NotNull(customerId);

        // ---------- 5. El cliente compra 2 unidades ----------
        var orderRequest = new OrderRequest(
            customerId!.Value,
            "Av. Siempre Viva 742",
            "Av. Siempre Viva 742",
            "Pedido del flujo E2E",
            new List<OrderItemRequest> { new OrderItemRequest(product!.Id, 2) });

        var createOrderResponse = await customerClient.PostAsJsonAsync("/api/orders", orderRequest);
        Assert.Equal(HttpStatusCode.Created, createOrderResponse.StatusCode);

        var order = await createOrderResponse.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(order);
        Assert.Equal(2401.00m, order!.TotalAmount);          // 2 x 1200,50
        Assert.Equal(OrderStatus.PENDING, order.Status);

        // ---------- 6. La orden se puede consultar por la API ----------
        var fetchedOrder = await GetJsonAsync<OrderResponse>(customerClient, $"/api/orders/{order.OrderId}");
        Assert.Equal(order.OrderId, fetchedOrder.OrderId);
        Assert.Equal(2401.00m, fetchedOrder.TotalAmount);
        var item = Assert.Single(fetchedOrder.Items);
        Assert.Equal(product.Id, item.ProductId);
        Assert.Equal(2, item.Quantity);

        // ---------- 7. El stock bajó de 5 a 3 (consultado por la API, no por la base) ----------
        var productAfterPurchase = await GetJsonAsync<ProductModel.ResponseProduct>(anonymous, $"/api/products/{product.Id}");
        Assert.Equal(3, productAfterPurchase.StockQuantity);

        // ---------- 8. Si intenta comprar más de lo que queda, la API lo rechaza ----------
        var tooManyRequest = new OrderRequest(
            customerId.Value,
            "Av. Siempre Viva 742",
            "Av. Siempre Viva 742",
            null,
            new List<OrderItemRequest> { new OrderItemRequest(product.Id, 4) });

        var rejectedResponse = await customerClient.PostAsJsonAsync("/api/orders", tooManyRequest);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedResponse.StatusCode);
        var errorText = await ReadErrorTextAsync(rejectedResponse);
        Assert.Contains("stock", errorText, StringComparison.OrdinalIgnoreCase);

        // ... y el stock quedó intacto
        var productFinal = await GetJsonAsync<ProductModel.ResponseProduct>(anonymous, $"/api/products/{product.Id}");
        Assert.Equal(3, productFinal.StockQuantity);
    }

    // =====================================================
    //  Helpers propios de este flujo
    // =====================================================

    /// <summary>
    /// Crea un usuario con rol Admin. No hay administradores precargados (admins.json no está
    /// en el repositorio), así que se da de alta con el UserManager de Identity de la propia app.
    /// Después inicia sesión por la API, igual que lo haría un administrador real.
    /// </summary>
    private async Task<(string Username, string Email)> CreateAdminUserAsync(string tag)
    {
        var username = $"admin-{tag}";
        var email = $"{username}@test.com";

        using var scope = Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        var user = new IdentityUser { UserName = username, Email = email };

        var createResult = await userManager.CreateAsync(user, TestPassword);
        Assert.True(createResult.Succeeded, string.Join(" | ", createResult.Errors.Select(e => e.Description)));

        var roleResult = await userManager.AddToRoleAsync(user, "Admin");
        Assert.True(roleResult.Succeeded, string.Join(" | ", roleResult.Errors.Select(e => e.Description)));

        return (username, email);
    }

    /// <summary>
    /// POST /api/auth/login con usuario y contraseña. Devuelve un HttpClient que ya lleva
    /// el token recibido en el header Authorization y el id de cliente (si el usuario es cliente).
    /// </summary>
    private async Task<(HttpClient Client, Guid? CustomerId)> LoginAsync(string username, string password)
    {
        var response = await CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var token = GetProperty(doc.RootElement, "token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var customerIdElement = GetProperty(GetProperty(doc.RootElement, "userInfo"), "customerId");
        Guid? customerId = customerIdElement.ValueKind == JsonValueKind.String
            ? customerIdElement.GetGuid()
            : null;

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, customerId);
    }

    /// <summary>GET que exige 200 OK y devuelve el cuerpo deserializado.</summary>
    private async Task<T> GetJsonAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        Assert.NotNull(body);
        return body!;
    }

    /// <summary>Busca una propiedad de un JSON sin distinguir mayúsculas (token / Token).</summary>
    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        throw new InvalidOperationException($"No se encontró la propiedad '{name}' en el JSON de respuesta.");
    }
}
