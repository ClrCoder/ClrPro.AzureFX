// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.Azure.LocalCredentialBridge.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

/// <summary>
///     The bridge web app factory for Integration tests.
/// </summary>
internal class BridgeTestAppFactory : WebApplicationFactory<Program>
{
    private readonly ITestOutputHelper _testOutput;

    public BridgeTestAppFactory(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(
            (_, config) =>
            {
                config.AddInMemoryCollection(
                    new Dictionary<string, string>
                    {
                        { "Logging:LogLevel:Default", "None" },
                        { "Logging:XUnit:LogLevel:Default", "Trace" },
                    });
            });
        builder.ConfigureServices(
            (_, services) =>
            {
                services.AddLogging(logging => logging.AddXUnit2(_testOutput));
            });
    }
}
