using Xunit;

namespace Dsw2025Tpi.IntegrationTests;

[Collection("IntegrationTests")]
public class SmokeTests
{
    private readonly CustomWebApplicationFactory _factory;

    public SmokeTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Application_ShouldStartSuccessfullyInTestingEnvironment()
    {
        var client = _factory.CreateClient();
        Assert.NotNull(client);
    }
}
