// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#:package Azure.Core@1.63.0

using System.Reflection;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

Console.WriteLine(
    $"Azure.Core {typeof(TokenCredential).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
var token = await credential.GetTokenAsync(
    new TokenRequestContext(["https://management.azure.com/.default"]))
    .ConfigureAwait(false);

if (string.IsNullOrWhiteSpace(token.Token) || token.ExpiresOn <= DateTimeOffset.UtcNow)
{
    throw new InvalidOperationException("The bridge returned an empty or expired access token.");
}

var expectedTenant = Environment.GetEnvironmentVariable("EXPECTED_TENANT_ID");
var expectedClient = Environment.GetEnvironmentVariable("EXPECTED_CLIENT_ID");
if (!string.IsNullOrEmpty(expectedTenant) || !string.IsNullOrEmpty(expectedClient))
{
    var payload = token.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
    payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
    using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
    var tenant = claims.RootElement.GetProperty("tid").GetString();
    var client = claims.RootElement.TryGetProperty("appid", out var appId)
        ? appId.GetString()
        : claims.RootElement.GetProperty("azp").GetString();
    if ((!string.IsNullOrEmpty(expectedTenant) && !string.Equals(tenant, expectedTenant, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrEmpty(expectedClient) && !string.Equals(client, expectedClient, StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("The token does not belong to the expected tenant and App Registration.");
    }
}

Console.WriteLine($"Managed Identity token acquired; expires at {token.ExpiresOn:O}.");
