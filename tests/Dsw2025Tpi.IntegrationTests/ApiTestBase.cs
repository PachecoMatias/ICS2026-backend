using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsw2025Tpi.Application.Services;
using Dsw2025Tpi.Data;
using Dsw2025Tpi.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Clase base de los tests de integración. Reúne lo que todos necesitan:
/// clientes HTTP (anónimo o con token), carga de datos en SQLite y lectura de la BD.
/// </summary>
public abstract class ApiTestBase
{
    protected CustomWebApplicationFactory Factory { get; }

    // La API devuelve JSON en camelCase y con los enums como texto ("PENDING").
    // Estas opciones permiten deserializar esas respuestas a nuestros DTOs.
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected ApiTestBase(CustomWebApplicationFactory factory)
    {
        Factory = factory;
    }

    // ---------- Clientes HTTP ----------

    /// <summary>Cliente sin credenciales (no manda el header Authorization).</summary>
    protected HttpClient CreateAnonymousClient() => Factory.CreateClient();

    /// <summary>
    /// Cliente con un token JWT válido para el rol indicado ("Admin" o "User").
    /// El token lo genera el JwtTokenService de la propia app, así que usa
    /// la misma clave, issuer y audience que la API valida.
    /// </summary>
    protected HttpClient CreateClientWithRole(string role, string customerId = "")
    {
        var tokenService = Factory.Services.GetRequiredService<JwtTokenService>();
        var token = tokenService.GenerateToken($"test-{role.ToLower()}", role, customerId);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---------- Datos de prueba ----------

    // Todos los tests comparten la misma base SQLite en memoria, así que cada test
    // genera valores únicos (SKU, nombres, mails) para no pisarse con los demás.
    protected static string UniqueId() => Guid.NewGuid().ToString("N")[..8];

    protected Product SeedProduct(string? name = null, int stock = 10, decimal price = 100m, bool isActive = true)
    {
        var id = UniqueId();
        var product = new Product($"SKU-{id}", name ?? $"Producto {id}", price, stock, "Producto de prueba", $"INT-{id}")
        {
            IsActive = isActive
        };

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
        db.Set<Product>().Add(product);
        db.SaveChanges();
        return product;
    }

    protected Customer SeedCustomer()
    {
        var id = UniqueId();
        var customer = new Customer
        {
            Name = $"Cliente {id}",
            Email = $"cliente-{id}@test.com",
            PhoneNumber = "3811234567"
        };

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
        db.Set<Customer>().Add(customer);
        db.SaveChanges();
        return customer;
    }

    // ---------- Lectura directa de la base ----------

    /// <summary>Ejecuta una consulta sobre la BD SQLite, con un DbContext nuevo (lee lo realmente guardado).</summary>
    protected T WithDb<T>(Func<Dsw2025TpiContext, T> query)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
        return query(db);
    }

    // ---------- Errores de la API ----------

    /// <summary>
    /// Lee el JSON de error que devuelve el middleware y junta todos sus textos.
    /// Se parsea el JSON (no se busca en el texto crudo) porque el middleware serializa
    /// con el encoder por defecto, que escapa tildes y comillas (por ejemplo "\u00F3").
    /// </summary>
    protected static async Task<string> ReadErrorTextAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var texts = doc.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => p.Value.GetString());

        return string.Join(" | ", texts);
    }
}
