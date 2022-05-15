// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.Azure.LocalCredentialBridge.Tests;

using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

[ProviderAlias("XUnit")]
public class XUnitLoggerProvider2 : XUnitLoggerProvider
{
    public XUnitLoggerProvider2(IMessageSink messageSink, XUnitLoggerOptions options)
        : base(messageSink, options)
    {
    }

    public XUnitLoggerProvider2(IMessageSinkAccessor accessor, XUnitLoggerOptions options)
        : base(accessor, options)
    {
    }

    public XUnitLoggerProvider2(ITestOutputHelper outputHelper, XUnitLoggerOptions options)
        : base(outputHelper, options)
    {
    }

    public XUnitLoggerProvider2(ITestOutputHelperAccessor accessor, XUnitLoggerOptions options)
        : base(accessor, options)
    {
    }
}
