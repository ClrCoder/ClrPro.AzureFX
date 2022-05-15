// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.Azure.LocalCredentialBridge.Tests;

using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

/// <summary>
///     The DI registration extensions for the extend XUnit logger.
/// </summary>
public static class XUnitLoggingDIExtensions
{
    /// <summary>
    ///     Adds extended XUnit logger.
    /// </summary>
    /// <remarks>
    ///     TODO: Contribute the fix to the original nuget package.
    /// </remarks>
    /// <param name="loggingBuilder">The logging builder.</param>
    /// <param name="testOutputHelper">The xunit test output helper.</param>
    /// <returns>The input logging builder to support fluent syntax.</returns>
    public static ILoggingBuilder AddXUnit2(this ILoggingBuilder loggingBuilder, ITestOutputHelper testOutputHelper)
    {
        loggingBuilder.AddXUnit2(
            testOutputHelper,
            _ =>
            {
            });
        return loggingBuilder;
    }

    /// <summary>
    ///     Adds an xunit logger to the logging builder.
    /// </summary>
    /// <param name="builder">The <see cref="ILoggingBuilder" /> to use.</param>
    /// <param name="outputHelper">The <see cref="ITestOutputHelper" /> to use.</param>
    /// <param name="configure">A delegate to a method to use to configure the logging options.</param>
    /// <returns>
    ///     The instance of <see cref="ILoggingBuilder" /> specified by <paramref name="builder" />.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="builder" />, <paramref name="outputHelper" /> OR <paramref name="configure" /> is
    ///     <see langword="null" />.
    /// </exception>
    public static ILoggingBuilder AddXUnit2(
        this ILoggingBuilder builder,
        ITestOutputHelper outputHelper,
        Action<XUnitLoggerOptions> configure)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (outputHelper == null)
        {
            throw new ArgumentNullException(nameof(outputHelper));
        }

        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new XUnitLoggerOptions();

        configure(options);

#pragma warning disable CA2000
        return builder.AddProvider(new XUnitLoggerProvider2(outputHelper, options));
#pragma warning restore CA2000
    }
}
