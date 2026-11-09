param(
    [string] $ImageName = "clrpro/azurefx-local-credential-bridge:local",
    [string] $ContainerName = "clrpro-azurefx-bridge",
    [string] $NetworkName = "clrpro-azurefx",
    [string] $AzProfileVolume = "clrpro-azurefx-az-profile",
    [string] $TokenFilesVolume = "clrpro-azurefx-token-files",
    [switch] $Rebuild
)

$ErrorActionPreference = "Stop"

function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $DockerArguments)

    & docker @DockerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed: docker $($DockerArguments -join ' ')"
    }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker is required. Install Docker Desktop or Docker Engine and try again."
}

Invoke-Docker build --file (Join-Path $PSScriptRoot "Dockerfile") --tag $ImageName $PSScriptRoot

& docker network inspect $NetworkName *> $null
if ($LASTEXITCODE -ne 0) {
    Invoke-Docker network create $NetworkName
}

foreach ($volumeName in @($AzProfileVolume, $TokenFilesVolume)) {
    & docker volume inspect $volumeName *> $null
    if ($LASTEXITCODE -ne 0) {
        Invoke-Docker volume create $volumeName
    }
}

$running = & docker container inspect --format "{{.State.Running}}" $ContainerName 2>$null
if ($LASTEXITCODE -eq 0) {
    if ($Rebuild) {
        if ($running -eq "true") {
            Invoke-Docker container stop --timeout 30 $ContainerName
        }
        Invoke-Docker container rm $ContainerName
        Write-Host "Recreating '$ContainerName' with the rebuilt image; named volumes are preserved."
    } elseif ($running -eq "true") {
        Write-Host "Container '$ContainerName' is already running; leaving it unchanged."
        exit 0
    } else {
        Invoke-Docker container start $ContainerName
        Write-Host "Started existing container '$ContainerName'."
        exit 0
    }
}

Invoke-Docker run `
    --detach `
    --name $ContainerName `
    --network $NetworkName `
    --restart unless-stopped `
    --mount "type=volume,source=$AzProfileVolume,target=/root/.Azure" `
    --mount "type=volume,source=$TokenFilesVolume,target=/var/opt/azcmagent/tokens" `
    $ImageName

Write-Host "Started '$ContainerName' on Docker network '$NetworkName'."
