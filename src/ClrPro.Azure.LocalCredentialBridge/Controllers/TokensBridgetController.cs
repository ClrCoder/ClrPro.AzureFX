// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.Azure.LocalCredentialBridge.Controllers;

using System.Globalization;
using global::Azure.Core;
using global::Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Azure;

[ApiController]
public class TokensBridgetController : ControllerBase
{
    private readonly ILogger<TokensBridgetController> _logger;
    private readonly TokenCredential _tokenCredential;

    public TokensBridgetController(TokenCredential tokenCredential, ILogger<TokensBridgetController> logger)
    {
        _tokenCredential = tokenCredential;
        _logger = logger;
    }

    [HttpGet("metadata/identity/oauth2/token")]
    public async Task<TokenResponse> Get(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new ArgumentNullException(nameof(resource));
        }

        var token = await _tokenCredential.GetTokenAsync(new TokenRequestContext(new[] { resource }), default)
            .ConfigureAwait(true);
        return new TokenResponse
        {
            access_token = token.Token,
            expires_on = token.ExpiresOn.ToString(CultureInfo.InvariantCulture),
        };
    }

    public class TokenResponse
    {
        public string? access_token { get; set; }

        public string? expires_on { get; set; }
    }
}
