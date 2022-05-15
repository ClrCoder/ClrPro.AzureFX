// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.Azure.LocalCredentialBridge.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

/// <summary>
///     Helps to build client application.
/// </summary>
public static class TestClientApplicationFactory
{
    /// <summary>
    ///     Initializes the client application host builder.
    /// </summary>
    /// <param name="testOutput">The test output helper.</param>
    /// <returns>The client application host builder.</returns>
    public static IHostBuilder CreateClientAppHost(
        ITestOutputHelper testOutput)
    {
        var hostBuilder = Host.CreateDefaultBuilder();
        hostBuilder.ConfigureServices(
            (_, services) =>
            {
                services.AddHttpClient();
            });
        hostBuilder.ConfigureLogging(logging => logging.AddXUnit2(testOutput));
        return hostBuilder;
    }
}
