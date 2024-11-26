// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.AzureFX.LocalCredentialBridge.Controllers;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

/// <summary>
///     Handles the Arc ManagedIdentity protocol and provides local <see cref="DefaultAzureCredential" /> tokens of the
///     host through this protocol.
/// </summary>
[ApiController]
public class TokensBridgetController : ControllerBase
{
    private readonly ILogger<TokensBridgetController> _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptionsMonitor<LocalCredentialBridgeOptions> _optionsMonitor;
    private readonly TokenCredential _tokenCredential;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TokensBridgetController" /> class.
    /// </summary>
    [SuppressMessage(
        "StyleCop.CSharp.DocumentationRules",
        "SA1611:Element parameters should be documented",
        Justification = "DI Dependencies.")]
    public TokensBridgetController(
        IMemoryCache memoryCache,
        IOptionsMonitor<LocalCredentialBridgeOptions> optionsMonitor,
        TokenCredential tokenCredential,
        ILogger<TokensBridgetController> logger)
    {
        _memoryCache = memoryCache;
        _optionsMonitor = optionsMonitor;
        _tokenCredential = tokenCredential;
        _logger = logger;
    }

    /// <summary>
    ///     Gets the token for the specified resource.
    /// </summary>
    /// <param name="resource">The resource to get token for.</param>
    /// <returns>The IMDS response with token or WWWAuthenticate challenge request.</returns>
    [HttpGet("metadata/identity/oauth2/token")]
    [SuppressMessage("Globalization", "CA1307:Specify StringComparison for clarity", Justification = "Reviewed.")]
    [SuppressMessage("Globalization", "CA1310:Specify StringComparison for correctness", Justification = "Reviewed.")]
    public async Task<ActionResult> Get(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new ArgumentNullException(nameof(resource));
        }

        var options = _optionsMonitor.CurrentValue;
        if (options.UseFileTokenAuth)
        {
            var localPath = Environment.ExpandEnvironmentVariables(options.LocalTokensPath!);
            if (!Directory.Exists(localPath))
            {
                Directory.CreateDirectory(localPath);
            }

            var authHeader = Request.Headers.Authorization;
            if (string.IsNullOrWhiteSpace(authHeader))
            {
                var fileName = $"{Guid.NewGuid().ToString()}.key";
                var localFileName = Path.Combine(localPath, fileName);
                var remoteFileName = Path.Combine(options.RemoteTokensPath!, fileName);

                // If it's unix path
                if (remoteFileName.Contains('/'))
                {
                    remoteFileName = remoteFileName.Replace('\\', '/');
                }

                await CreateTokenFileAndAddToCache(localFileName, TimeSpan.FromSeconds(options.AuthTokenTimeoutSeconds))
                    .ConfigureAwait(false);

                Response.Headers.Add("WWW-Authenticate", $"TokenFile={remoteFileName}");
                return Unauthorized();
            }
            else
            {
                var authPassed = false;
                string authHeaderString = authHeader;
                if (authHeaderString.StartsWith("Basic "))
                {
                    var parts = authHeaderString.Split(" ", 2);
                    if (parts.Length == 2)
                    {
                        var secret = parts[1];
                        if (_memoryCache.TryGetValue(secret, out _))
                        {
                            _memoryCache.Remove(secret);
                            authPassed = true;
                        }
                    }
                }

                if (!authPassed)
                {
                    return Unauthorized();
                }
            }
        }

        var token = await _tokenCredential.GetTokenAsync(new TokenRequestContext(new[] { resource }), default)
            .ConfigureAwait(true);
        return Ok(
            new TokenResponse
            {
                access_token = token.Token,
                expires_on = token.ExpiresOn.ToUnixTimeSeconds(),
                resource = resource
            });
    }

    private static void DeleteTokenFile(string fileName)
    {
        try
        {
            System.IO.File.Delete(fileName);
        }
        catch
        {
            // Do nothing.
        }
    }

    private async Task CreateTokenFileAndAddToCache(string localFileName, TimeSpan cacheTimeout)
    {
        var randomValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await System.IO.File.WriteAllTextAsync(localFileName, randomValue).ConfigureAwait(false);
        using var cacheEntry = _memoryCache.CreateEntry(randomValue);
        cacheEntry.AbsoluteExpiration =
            DateTimeOffset.UtcNow + cacheTimeout;
        cacheEntry.PostEvictionCallbacks.Add(
            new PostEvictionCallbackRegistration
            {
                EvictionCallback = (key, value, reason, state) =>
                {
                    DeleteTokenFile((string)value);
                },
            });
        cacheEntry.Value = localFileName;
    }

    /// <summary>
    ///     The response DTO contract.
    /// </summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "Reviewed.")]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "DTO")]
    [SuppressMessage(
        "StyleCop.CSharp.NamingRules",
        "SA1300:Element should begin with upper-case letter",
        Justification = "DTO")]
    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "DTO")]
    public class TokenResponse
    {
        /// <summary>
        ///     The token value.
        /// </summary>
        public string? access_token { get; set; }

        /// <summary>
        ///     The expiration time.
        /// </summary>
        public long? expires_on { get; set; }

        /// <summary>
        ///     The resource.
        /// </summary>
        public string? resource { get; set; }
    }
}
