using System.Data.Common;
using Dsw2025Tpi.Api;
using Dsw2025Tpi.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Dsw2025Tpi.IntegrationTests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _connection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove existing Dsw2025TpiContext registrations
            var dswDescriptors = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<Dsw2025TpiContext>) ||
                d.ServiceType == typeof(Dsw2025TpiContext) ||
                (d.ServiceType.FullName != null && d.ServiceType.FullName.Contains("Dsw2025TpiContext"))).ToList();

            foreach (var descriptor in dswDescriptors)
            {
                services.Remove(descriptor);
            }

            // Remove existing AuthenticateContext registrations
            var authDescriptors = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<AuthenticateContext>) ||
                d.ServiceType == typeof(AuthenticateContext) ||
                (d.ServiceType.FullName != null && d.ServiceType.FullName.Contains("AuthenticateContext"))).ToList();

            foreach (var descriptor in authDescriptors)
            {
                services.Remove(descriptor);
            }

            if (_connection == null)
            {
                _connection = new SqliteConnection("DataSource=:memory:");
                _connection.Open();
            }

            services.AddDbContext<Dsw2025TpiContext>(options =>
            {
                options.UseSqlite(_connection);
            });

            services.AddDbContext<AuthenticateContext>(options =>
            {
                options.UseSqlite(_connection);
            });

            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var authDb = scope.ServiceProvider.GetRequiredService<AuthenticateContext>();
            authDb.Database.EnsureCreated();

            var appDb = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
            var creator = appDb.Database.GetService<IDatabaseCreator>() as IRelationalDatabaseCreator;
            creator?.CreateTables();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
