using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TrustVault.Api.Tests;

public class HealthEndpointTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(
        WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsHealthyResponse()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var content =
            await response.Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(content);

        Assert.Equal(
            "healthy",
            json.RootElement
                .GetProperty("status")
                .GetString());

        Assert.Equal(
            "TrustVault API",
            json.RootElement
                .GetProperty("application")
                .GetString());
    }
}