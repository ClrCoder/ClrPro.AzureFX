// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.AzureFX.LocalCredentialBridge.Tests;

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Async test fixture disposal has no synchronization context.")]
public class BridgeProtocolTests
{
    private static readonly Uri TokenUrl = new(
        "/metadata/identity/oauth2/token?resource=https%3A%2F%2Fmanagement.azure.com%2F",
        UriKind.Relative);

    [Fact]
    public async Task ChallengeIsSingleUseAndDeletesItsFile()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        var (path, secret) = await GetChallengeAsync(client).ConfigureAwait(false);
        using var response = await SendAuthenticatedAsync(client, secret).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(File.Exists(path));
        Assert.True(response.Headers.CacheControl!.NoStore);
        var token = await response.Content.ReadFromJsonAsync<TokenDto>().ConfigureAwait(false);
        Assert.Equal("test-access-token", token!.AccessToken);
        Assert.Equal("https://management.azure.com/", token.Resource);
        using var replay = await SendAuthenticatedAsync(client, secret).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(1, app.Credential.Calls);
    }

    [Fact]
    public async Task ConcurrentRedemptionHasExactlyOneWinner()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        var (_, secret) = await GetChallengeAsync(client).ConfigureAwait(false);
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => SendAuthenticatedAsync(client, secret))).ConfigureAwait(false);
        try
        {
            Assert.Single(responses.Where(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Equal(1, app.Credential.Calls);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task ExpiredChallengesAreRejectedAndCleaned()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        var (path, secret) = await GetChallengeAsync(client).ConfigureAwait(false);
        app.Clock.Advance(TimeSpan.FromSeconds(10));
        using var expired = await SendAuthenticatedAsync(client, secret).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.False(File.Exists(path));
        Assert.Equal(0, app.Credential.Calls);
    }

    [Fact]
    public async Task CleanupRemovesExpiredFilesWithoutAnotherRequest()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        var (path, _) = await GetChallengeAsync(client).ConfigureAwait(false);
        app.Clock.Advance(TimeSpan.FromSeconds(11));
        await app.Services.GetRequiredService<ChallengeStore>().CleanupAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task BackgroundTimerCleansAbandonedChallenges()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        var (path, _) = await GetChallengeAsync(client).ConfigureAwait(false);
        app.Clock.Advance(TimeSpan.FromSeconds(11));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (File.Exists(path) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ShutdownCleansPendingChallenges()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        var (path, _) = await GetChallengeAsync(client).ConfigureAwait(false);
        await app.Services.GetRequiredService<ChallengeStore>().StopAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ChallengeFileFailureReturnsServiceUnavailable()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        Directory.Delete(app.TokenDirectory);
        try
        {
            using var response = await client.GetAsync(TokenUrl).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(0, app.Credential.Calls);
        }
        finally
        {
            Directory.CreateDirectory(app.TokenDirectory);
        }
    }

    [Fact]
    public async Task ChallengeLimitIsReleasedAfterRedemption()
    {
        await using var app = new TestApp(o => o.MaxPendingChallenges = 1);
        using var client = app.CreateClient();
        var (_, secret) = await GetChallengeAsync(client).ConfigureAwait(false);
        using var limited = await client.GetAsync(TokenUrl).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), limited.Headers.RetryAfter!.Delta);
        using var redeemed = await SendAuthenticatedAsync(client, secret).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        await GetChallengeAsync(client).ConfigureAwait(false);
    }

    [Fact]
    public async Task FailedDeletionIsRetriedWithoutAllowingReplay()
    {
        await using var app = new TestApp(o => o.MaxPendingChallenges = 1);
        using var client = app.CreateClient();
        var (path, secret) = await GetChallengeAsync(client).ConfigureAwait(false);
        File.Delete(path);
        Directory.CreateDirectory(path);
        using var redeemed = await SendAuthenticatedAsync(client, secret).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        using var replay = await SendAuthenticatedAsync(client, secret).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var limited = await client.GetAsync(TokenUrl).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Directory.Delete(path);
        await app.Services.GetRequiredService<ChallengeStore>().CleanupAsync(CancellationToken.None).ConfigureAwait(false);
        await GetChallengeAsync(client).ConfigureAwait(false);
    }

    [Fact]
    public async Task StartupDeletesOnlyBridgeOwnedFiles()
    {
        await using var app = new TestApp();
        var stale = Path.Combine(app.TokenDirectory, $"{Guid.NewGuid():D}.key");
        var unrelated = Path.Combine(app.TokenDirectory, "unrelated.key");
        await File.WriteAllTextAsync(stale, "stale").ConfigureAwait(false);
        await File.WriteAllTextAsync(unrelated, "keep").ConfigureAwait(false);
        using var client = app.CreateClient();
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(unrelated));
    }

    [Theory]
    [InlineData("Bearer ignored")]
    [InlineData("Basic ")]
    [InlineData("Basic unknown")]
    [InlineData("Basic unknown extra")]
    public async Task InvalidAuthorizationDoesNotIssueTokens(string header)
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, TokenUrl);
        request.Headers.TryAddWithoutValidation("Authorization", header);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, app.Credential.Calls);
    }

    [Fact]
    public async Task MultipleAuthorizationHeadersCannotRedeemChallenge()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        var (_, secret) = await GetChallengeAsync(client).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, TokenUrl);
        request.Headers.TryAddWithoutValidation("Authorization", new[] { $"Basic {secret}", $"Basic {secret}" });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var valid = await SendAuthenticatedAsync(client, secret).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Theory]
    [InlineData("/metadata/identity/oauth2/token")]
    [InlineData("/metadata/identity/oauth2/token?resource=")]
    [InlineData("/metadata/identity/oauth2/token?resource=%20")]
    public async Task MissingOrBlankResourceReturnsBadRequest(string requestPath)
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        using var response = await client.GetAsync(new Uri(requestPath, UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Credential.Calls);
        Assert.Empty(Directory.EnumerateFiles(app.TokenDirectory));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.BadGateway)]
    public async Task CredentialErrorsAreExplicitAndDoNotExposeDetails(bool authenticationFailed, HttpStatusCode status)
    {
        await using var app = new TestApp(o => o.UseFileTokenAuth = false);
        app.Credential.Failure = authenticationFailed
            ? new AuthenticationFailedException("sensitive-provider-details")
            : new CredentialUnavailableException("sensitive-provider-details");
        using var client = app.CreateClient();
        using var response = await client.GetAsync(TokenUrl).ConfigureAwait(false);
        Assert.Equal(status, response.StatusCode);
        Assert.DoesNotContain(
            "sensitive-provider-details",
            await response.Content.ReadAsStringAsync().ConfigureAwait(false),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthDoesNotRequireAzureCredentials()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, app.Credential.Calls);
    }

    [Theory]
    [InlineData("LocalTokensPath")]
    [InlineData("RemoteTokensPath")]
    [InlineData("AuthTokenTimeoutSeconds")]
    [InlineData("MaxPendingChallenges")]
    [InlineData("CredentialSource")]
    public async Task InvalidOptionsFailStartup(string option)
    {
        await using var app = new TestApp(o =>
        {
            switch (option)
            {
                case "LocalTokensPath": o.LocalTokensPath = "relative"; break;
                case "RemoteTokensPath": o.RemoteTokensPath = "/tokens\r\ninvalid"; break;
                case "AuthTokenTimeoutSeconds": o.AuthTokenTimeoutSeconds = 0; break;
                case "MaxPendingChallenges": o.MaxPendingChallenges = 0; break;
                case "CredentialSource": o.CredentialSource = "unknown"; break;
            }
        });
        Assert.Throws<OptionsValidationException>(() => app.CreateClient());
    }

    [Fact]
    public async Task CancelledChallengeCreationDoesNotLeaveFiles()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => app.Services.GetRequiredService<ChallengeStore>().CreateAsync(cancellation.Token)).ConfigureAwait(false);
        Assert.Empty(Directory.EnumerateFiles(app.TokenDirectory));
    }

    [Fact]
    public async Task RequestCancellationIsPassedToAzureCredential()
    {
        await using var app = new TestApp(o => o.UseFileTokenAuth = false);
        using var client = app.CreateClient();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var controller = ActivatorUtilities.CreateInstance<Controllers.TokensBridgetController>(app.Services);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.Get("https://management.azure.com/")).ConfigureAwait(false);
        Assert.Equal(cancellation.Token, app.Credential.LastCancellationToken);
    }

    private static async Task<(string Path, string Secret)> GetChallengeAsync(HttpClient client)
    {
        using var response = await client.GetAsync(TokenUrl).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var header = Assert.Single(response.Headers.GetValues("WWW-Authenticate"));
        Assert.StartsWith("TokenFile=", header, StringComparison.Ordinal);
        var path = header["TokenFile=".Length..];
        Assert.EndsWith(".key", path, StringComparison.Ordinal);
        return (path, await File.ReadAllTextAsync(path).ConfigureAwait(false));
    }

    private static async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpClient client, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TokenUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {secret}");
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private sealed class TestApp : WebApplicationFactory<Program>
    {
        private readonly Action<LocalCredentialBridgeOptions>? _configure;

        public TestApp(Action<LocalCredentialBridgeOptions>? configure = null)
        {
            _configure = configure;
            Directory.CreateDirectory(TokenDirectory);
        }

        public string TokenDirectory { get; } = Path.Combine(Path.GetTempPath(), $"azurefx-tests-{Guid.NewGuid():N}");

        public TestClock Clock { get; } = new();

        public TestCredential Credential { get; } = new();

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(TokenDirectory, recursive: true);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TokenCredential>();
                services.AddSingleton<TokenCredential>(Credential);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.PostConfigure<LocalCredentialBridgeOptions>(o =>
                {
                    o.LocalTokensPath = TokenDirectory;
                    o.RemoteTokensPath = TokenDirectory;
                    _configure?.Invoke(o);
                });
            });
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class TestCredential : TokenCredential
    {
        private int _calls;

        public int Calls => _calls;

        public Exception? Failure { get; set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            LastCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            if (Failure is not null)
            {
                throw Failure;
            }

            return new AccessToken("test-access-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class TokenDto
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        public string? Resource { get; set; }
    }
}
