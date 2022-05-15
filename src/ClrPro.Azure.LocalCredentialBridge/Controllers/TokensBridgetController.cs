// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.Azure.LocalCredentialBridge.Controllers;

using Microsoft.AspNetCore.Mvc;

[ApiController]
public class TokensBridgetController : ControllerBase
{
    private readonly ILogger<TokensBridgetController> _logger;

    public TokensBridgetController(ILogger<TokensBridgetController> logger)
    {
        _logger = logger;
    }

    [HttpGet("metadata/identity/oauth2/token")]
    public async Task<ActionResult> Get()
    {
        return Content("Hello world!");
    }
}
