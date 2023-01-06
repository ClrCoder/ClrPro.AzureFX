// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.AzureFX.LocalCredentialBridge.Tests;

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

[Trait("Category", "Manual")]
public class LocalCredentialBridgeIntegrationTests
{
    private readonly ITestOutputHelper _testOutput;

    public LocalCredentialBridgeIntegrationTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Reviewed")]
    [SuppressMessage(
        "StyleCop.CSharp.NamingRules",
        "SA1312:Variable names should begin with lower-case letter",
        Justification = "Reviewed.")]
    public async Task SimpleTest()
    {
        var baseAppFactory = new BridgeTestAppFactory(_testOutput);
        await using var _ = baseAppFactory.ConfigureAwait(false);
        var configuredAppFactory = baseAppFactory.WithWebHostBuilder(
            hostBuilder =>
            {
                hostBuilder.ConfigureAppConfiguration(
                    (_, config) =>
                    {
                        config.AddInMemoryCollection(
                            new Dictionary<string, string>
                            {
                                {
                                    "LocalCredentialBridge:RemoteTokensPath",
                                    Environment.ExpandEnvironmentVariables("%USERPROFILE%/.LocalCredentialBridgeTokens")
                                },
                            });
                    });
            });

        await using var __ = configuredAppFactory.ConfigureAwait(false);
        Environment.SetEnvironmentVariable(
            "IDENTITY_ENDPOINT",
            "http://host.docker.internal:40342/metadata/identity/oauth2/token");
        Environment.SetEnvironmentVariable("IMDS_ENDPOINT", "http://host.docker.internal:40342");

        const string credClientName = "CredClient";
        var clientAppBuilder = TestClientApplicationFactory.CreateClientAppHost(_testOutput);
        clientAppBuilder.ConfigureServices(
            (_, services) =>
            {
                services.AddHttpClient(credClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => configuredAppFactory.Server.CreateHandler());
            });

        using var clientApp = clientAppBuilder.Build();
        var httpClientFactory = clientApp.Services.GetRequiredService<IHttpClientFactory>();
        using var httpClient = httpClientFactory.CreateClient(credClientName);
        var msiCredential = new ManagedIdentityCredential(
            (string?)null,
            new TokenCredentialOptions
            {
                Transport = new HttpClientTransport(httpClient),
            });
        var secretClient = new SecretClient(new Uri("https://kv-identity-tests.vault.azure.net/"), msiCredential);
        var testSecret = await secretClient.GetSecretAsync("test-secret").ConfigureAwait(false);
        testSecret.Value.Value.Should().Be("42");
    }

    [Fact]
    public async Task DefaultCredentialSelfTest()
    {
        var defaultCredential = new DefaultAzureCredential();
        var secretClient = new SecretClient(new Uri("https://kv-identity-tests.vault.azure.net/"), defaultCredential);
        var testSecret = await secretClient.GetSecretAsync("test-secret").ConfigureAwait(false);
        testSecret.Value.Value.Should().Be("42");
    }

    [Fact]
    public async Task AuthChallengeShouldBeRaised()
    {
        var bridgeTestAppFactory = new BridgeTestAppFactory(_testOutput);
        await using var _ = bridgeTestAppFactory.ConfigureAwait(false);
        using var client = bridgeTestAppFactory.Server.CreateClient();
        var response = await client
            .GetAsync(new Uri("http://host.docker.internal:40342/metadata/identity/oauth2/token?resource=tst"))
            .ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var contentString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        contentString.Should().StartWith("WWWAuthenticate");
    }
}
