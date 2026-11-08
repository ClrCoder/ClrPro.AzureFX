param(
    [switch] $RunTest
)

$ErrorActionPreference = "Stop"
$credentialsPath = Join-Path (Split-Path $PSScriptRoot -Parent) "e2e-credentials.json"
if (Test-Path -LiteralPath $credentialsPath) {
    throw "e2e-credentials.json already exists. Remove it explicitly before creating new credentials."
}

$tenantId = Read-Host "Directory (tenant) ID"
$clientId = Read-Host "Application (client) ID"
foreach ($id in @($tenantId, $clientId)) {
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($id, [ref] $parsed)) {
        throw "Tenant and client IDs must be GUIDs. No credentials file was created."
    }
}

$clientSecret = Read-Host "Client secret value (not the secret ID)" -MaskInput
if ([string]::IsNullOrWhiteSpace($clientSecret)) {
    throw "Client secret must not be empty. No credentials file was created."
}

$stream = $null
$writer = $null
try {
    $options = [IO.FileStreamOptions]::new()
    $options.Mode = [IO.FileMode]::CreateNew
    $options.Access = [IO.FileAccess]::Write
    if (-not $IsWindows) {
        $options.UnixCreateMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
    }
    $stream = [IO.FileStream]::new($credentialsPath, $options)
    $writer = [IO.StreamWriter]::new($stream)
    $writer.Write((@{
        TenantId = $tenantId
        ClientId = $clientId
        ClientSecret = $clientSecret
    } | ConvertTo-Json))
} finally {
    if ($null -ne $writer) { $writer.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
    $clientSecret = $null
}

Write-Host "Created checkout-root e2e-credentials.json. Keep it private; it persists across test wipes."
if ($IsWindows) {
    Write-Host "On Windows, ensure the file's NTFS permissions allow only your account and trusted administrators."
}
if ($RunTest) {
    & (Join-Path $PSScriptRoot "end-to-end.ps1")
} else {
    Write-Host "Run ./test/end-to-end.ps1 to start the test."
}
