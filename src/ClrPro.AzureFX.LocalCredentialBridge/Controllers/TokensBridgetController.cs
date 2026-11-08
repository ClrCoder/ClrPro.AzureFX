// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.AzureFX.LocalCredentialBridge.Controllers;

using System.Diagnostics.CodeAnalysis;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

/// <summary>
///     Handles the Arc ManagedIdentity protocol and provides local <see cref="DefaultAzureCredential" /> tokens of the
///     host through this protocol.
/// </summary>
[ApiController]
public class TokensBridgetController : ControllerBase
{
    private readonly ILogger<TokensBridgetController> _logger;
    private readonly ChallengeStore _challengeStore;
    private readonly LocalCredentialBridgeOptions _options;
    private readonly TokenCredential _tokenCredential;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TokensBridgetController" /> class.
    /// </summary>
    [SuppressMessage(
        "StyleCop.CSharp.DocumentationRules",
        "SA1611:Element parameters should be documented",
        Justification = "DI Dependencies.")]
    public TokensBridgetController(
        ChallengeStore challengeStore,
        IOptions<LocalCredentialBridgeOptions> options,
        TokenCredential tokenCredential,
        ILogger<TokensBridgetController> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _challengeStore = challengeStore;
        _options = options.Value;
        _tokenCredential = tokenCredential;
        _logger = logger;
    }

    /// <summary>
    ///     Gets the token for the specified resource.
    /// </summary>
    /// <param name="resource">The resource to get token for.</param>
    /// <returns>The IMDS response with token or WWWAuthenticate challenge request.</returns>
    [HttpGet("metadata/identity/oauth2/token")]
    public async Task<ActionResult> Get(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return BadRequest(new { error = "invalid_resource", error_description = "A nonempty resource is required." });
        }

        var cancellationToken = HttpContext.RequestAborted;
        if (_options.UseFileTokenAuth)
        {
            var authHeader = Request.Headers.Authorization;
            if (authHeader.Count == 0)
            {
                string? remoteFile;
                try
                {
                    remoteFile = await _challengeStore.CreateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(exception, "Could not create an authentication challenge file.");
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "challenge_unavailable" });
                }

                if (remoteFile is null)
                {
                    Response.Headers.RetryAfter = "1";
                    return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "challenge_limit_reached" });
                }

                Response.Headers.WWWAuthenticate = $"TokenFile={remoteFile}";
                return Unauthorized();
            }

            var header = authHeader.Count == 1 ? authHeader[0] : null;
            if (header is null
                || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                || !await _challengeStore.RedeemAsync(header[6..], cancellationToken).ConfigureAwait(false))
            {
                return Unauthorized();
            }
        }

        AccessToken token;
        try
        {
            token = await _tokenCredential.GetTokenAsync(new TokenRequestContext([resource]), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CredentialUnavailableException)
        {
            _logger.LogWarning("The configured Azure credential is unavailable. Sign in to the configured credential source.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "credential_unavailable" });
        }
        catch (AuthenticationFailedException)
        {
            _logger.LogWarning("Azure credential authentication failed. Check the signed-in account.");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "authentication_failed" });
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(
            new TokenResponse
            {
                access_token = token.Token,
                expires_on = token.ExpiresOn.ToUnixTimeSeconds(),
                resource = resource,
            });
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
