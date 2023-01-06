// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.AzureFX.LocalCredentialBridge;

/// <summary>
///     The options for the application.
/// </summary>
public class LocalCredentialBridgeOptions
{
    /// <summary>
    ///     The path of the options in the settings subsystem.
    /// </summary>
    public const string OptionsPath = "LocalCredentialBridge";

    /// <summary>
    ///     Specify if the FileToken auth challenge is enabled.
    /// </summary>
    public bool UseFileTokenAuth { get; set; } = true;

    /// <summary>
    ///     The path in the local file system where to store the authentication tokens.
    /// </summary>
    /// <remarks>
    ///     The path support variables expansion.
    /// </remarks>
    public string? LocalTokensPath { get; set; } = "%USERPROFILE%/.LocalCredentialBridgeTokens";

    /// <summary>
    ///     The path mapped to the bridge client OS.
    /// </summary>
    public string? RemoteTokensPath { get; set; } = "/var/opt/azcmagent/tokens";

    /// <summary>
    ///     The timeout for the authentication token.
    /// </summary>
    public int AuthTokenTimeoutSeconds { get; set; } = 10;
}
