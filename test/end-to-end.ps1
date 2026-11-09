param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$composeFile = Join-Path $repoRoot "examples/docker-compose.yml"
$container = "clrpro-azurefx-e2e-bridge"
$network = "clrpro-azurefx-e2e"
$profileVolume = "clrpro-azurefx-e2e-az-profile"
$tokenVolume = "clrpro-azurefx-e2e-token-files"
$bridgeImage = "clrpro/azurefx-local-credential-bridge:e2e"
$demoImage = "clrpro/azurefx-managed-identity-demo:e2e"
$core163Image = "clrpro/azurefx-core163-demo:e2e"
$composeProject = "clrpro-azurefx-e2e"

function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $DockerArguments)

    & docker @DockerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed: docker $($DockerArguments -join ' ')"
    }
}

function Invoke-Compose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $ComposeArguments)

    Invoke-Docker compose --project-name $composeProject --file $composeFile @ComposeArguments
}

function Test-IdentityClients {
    Invoke-Compose run --rm --no-deps managed-identity-demo
    Invoke-Compose run --rm --no-deps azure-core-163-demo
}

function Remove-TestResources {
    Invoke-Compose down --remove-orphans

    $containers = @(Invoke-Docker container ls --all --format "{{.Names}}")
    if ($containers -contains $container) {
        Invoke-Docker container rm --force $container
    }

    $volumes = @(Invoke-Docker volume ls --format "{{.Name}}")
    foreach ($name in @($profileVolume, $tokenVolume)) {
        if ($volumes -contains $name) {
            Invoke-Docker volume rm $name
        }
    }

    $networks = @(Invoke-Docker network ls --format "{{.Name}}")
    if ($networks -contains $network) {
        Invoke-Docker network rm $network
    }

    $images = @(Invoke-Docker image ls --format "{{.Repository}}:{{.Tag}}")
    foreach ($name in @($bridgeImage, $demoImage, $core163Image)) {
        if ($images -contains $name) {
            Invoke-Docker image rm $name
        }
    }
}

function Start-TestBridge {
    param([switch] $Rebuild)

    & pwsh -NoLogo -NoProfile -File (Join-Path $repoRoot "start-container.ps1") `
        -ImageName $bridgeImage -ContainerName $container -NetworkName $network `
        -AzProfileVolume $profileVolume -TokenFilesVolume $tokenVolume -Rebuild:$Rebuild
    if ($LASTEXITCODE -ne 0) {
        throw "The sidecar startup script failed."
    }
}

function Wait-TestBridge {
    $probe = @'
$ErrorActionPreference = "Stop"
$deadline = [DateTime]::UtcNow.AddSeconds(60)
while ([DateTime]::UtcNow -lt $deadline) {
    try {
        $response = Invoke-WebRequest http://localhost:40342/health -TimeoutSec 5
        if ($response.StatusCode -eq 200) { exit 0 }
    } catch [System.Net.Http.HttpRequestException] {
        Start-Sleep -Seconds 1
    }
}
throw "The bridge did not become ready within 60 seconds."
'@
    Invoke-Docker exec $container pwsh -NoLogo -NoProfile -Command $probe
}

$credentialsPath = Join-Path $repoRoot "e2e-credentials.json"
if (-not (Test-Path -LiteralPath $credentialsPath -PathType Leaf)) {
    throw "Create checkout-root e2e-credentials.json using ./test/set-credentials.ps1 before running the test."
}
try {
    $credentials = Get-Content -LiteralPath $credentialsPath -Raw | ConvertFrom-Json -AsHashtable
} catch {
    throw "Could not read e2e-credentials.json as JSON. Check its format and file permissions."
}
if ($credentials -isnot [System.Collections.IDictionary]) {
    throw "e2e-credentials.json must contain a JSON object."
}
foreach ($name in @("TenantId", "ClientId", "ClientSecret")) {
    if ($credentials[$name] -isnot [string] -or [string]::IsNullOrWhiteSpace($credentials[$name])) {
        throw "e2e-credentials.json must contain a nonempty string field '$name'."
    }
}

foreach ($name in @("TenantId", "ClientId")) {
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($credentials[$name], [ref] $parsed)) {
        throw "e2e-credentials.json field '$name' must be a GUID."
    }
}

Invoke-Docker info --format "{{.ServerVersion}}"
Invoke-Docker compose version

$composeEnvironment = @{
    AZUREFX_BRIDGE_CONTAINER = $container
    AZUREFX_NETWORK = $network
    AZUREFX_TOKEN_VOLUME = $tokenVolume
    AZUREFX_DEMO_IMAGE = $demoImage
    AZUREFX_CORE163_IMAGE = $core163Image
    AZUREFX_EXPECTED_TENANT_ID = $credentials.TenantId
    AZUREFX_EXPECTED_CLIENT_ID = $credentials.ClientId
}
$previousEnvironment = @{}
foreach ($name in $composeEnvironment.Keys) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $composeEnvironment[$name])
}

try {
    Write-Host "Wiping dedicated E2E containers, volumes, network, and image tags."
    Remove-TestResources
    Start-TestBridge
    Wait-TestBridge

    Write-Host "Verifying that rerunning startup does not restart the bridge."
    $startedAt = Invoke-Docker container inspect --format "{{.State.StartedAt}}" $container
    Start-TestBridge
    $startedAgainAt = Invoke-Docker container inspect --format "{{.State.StartedAt}}" $container
    if ($startedAt -ne $startedAgainAt) {
        throw "Startup unexpectedly restarted the running bridge."
    }

    Write-Host "Signing in to Azure PowerShell using the test App Registration."
    $login = @'
$ErrorActionPreference = "Stop"
$details = [Console]::In.ReadToEnd() | ConvertFrom-Json
$secret = ConvertTo-SecureString $details.ClientSecret -AsPlainText -Force
$credential = [PSCredential]::new($details.ClientId, $secret)
Enable-AzContextAutosave -Scope CurrentUser | Out-Null
Connect-AzAccount -ServicePrincipal -Tenant $details.TenantId -Credential $credential -Scope CurrentUser -SkipContextPopulation | Out-Null
if (-not (Get-AzContext)) { throw "Azure PowerShell did not save an account context." }
'@
    @{
        TenantId = $credentials.TenantId
        ClientId = $credentials.ClientId
        ClientSecret = $credentials.ClientSecret
    } | ConvertTo-Json -Compress | & docker exec --interactive $container pwsh -NoLogo -NoProfile -Command $login
    if ($LASTEXITCODE -ne 0) {
        throw "Azure PowerShell sign-in failed."
    }

    Write-Host "Building and running the Compose ManagedIdentityCredential client."
    Invoke-Compose build managed-identity-demo azure-core-163-demo
    Test-IdentityClients

    Write-Host "Recreating the bridge with the persisted profile, without signing in again."
    $originalId = Invoke-Docker container inspect --format "{{.Id}}" $container
    Start-TestBridge -Rebuild
    $rebuiltId = Invoke-Docker container inspect --format "{{.Id}}" $container
    if ($originalId -eq $rebuiltId) {
        throw "Rebuild did not recreate the bridge container."
    }
    Wait-TestBridge
    Test-IdentityClients

    Write-Host "Verifying startup of an existing stopped bridge."
    Invoke-Docker container stop $container
    Start-TestBridge
    Wait-TestBridge
    Test-IdentityClients

    Write-Host "End-to-end test passed: token identity, profile persistence, and startup behavior."
} finally {
    try {
        Write-Host "Removing dedicated E2E resources, including persisted test credentials."
        Remove-TestResources
    } finally {
        $credentials = $null
        foreach ($name in $previousEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
        }
    }
}
