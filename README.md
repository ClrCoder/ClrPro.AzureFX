# ClrPro.AzureFX Local Credential Bridge

The Local Credential Bridge is a developer-sidecar for Docker Compose projects. It lets an application container use the Azure identity of a developer who signed in with Azure PowerShell, through the Azure Arc Managed Identity protocol.

The repository is the distribution: clone it and build the container locally. No prebuilt bridge image or binary distribution is required.

## Requirements

- Docker Engine or Docker Desktop, with Docker Compose v2
- PowerShell 7 on the host to run `start-container.ps1`
- An interactive Azure account with permission to access the Azure resources your application needs

The bridge image includes .NET 10, PowerShell, and the Az PowerShell module. Its Azure profile is kept in a Docker volume, so it remains available when the bridge container is recreated.

## Start the sidecar

From the repository root, run:

```powershell
./start-container.ps1
```

By default this builds `clrpro/azurefx-local-credential-bridge:local`, creates (if needed) the `clrpro-azurefx` network and the `clrpro-azurefx-az-profile` and `clrpro-azurefx-token-files` volumes, and starts `clrpro-azurefx-bridge`. The bridge does not publish its port to the host; it is reachable to containers attached to its Docker network. Running the script again does not restart an already running bridge. If the existing container is stopped, the script starts it.

If you change the script's resource-name parameters, update the matching external network, volume, and bridge hostname in your Compose file. To rebuild the container after changing the bridge source, remove the existing container and rerun the script; the named volumes (including the signed-in account) are preserved:

```powershell
docker rm --force clrpro-azurefx-bridge
./start-container.ps1
```

Sign in from PowerShell inside the running sidecar:

```powershell
docker exec -it clrpro-azurefx-bridge pwsh
Connect-AzAccount -UseDeviceAuthentication
Get-AzContext
```

Complete the device-code sign-in in your browser. The Az account and token cache are stored in the `clrpro-azurefx-az-profile` volume mounted at `/root/.Azure`. To change the signed-in account, run `Disconnect-AzAccount` and sign in again from the container.

The bridge uses `DefaultAzureCredential`, which can obtain tokens from the signed-in Azure PowerShell context. It exposes the managed-identity token endpoint on port `40342` inside the Docker network and uses a short-lived file-token challenge. The token exchange uses a separate shared volume so clients can read the challenge file without receiving the Az profile volume.

## Verify the Managed Identity flow

The included demo is a .NET application that requests an Azure Resource Manager token using `ManagedIdentityCredential`. It exercises the Azure SDK's managed identity challenge flow against the bridge.

From the repository root, run:

```powershell
docker compose -f examples/docker-compose.yml run --build --rm managed-identity-demo
```

On success, the demo prints the token's expiry time. It does not print the token itself. The compose service joins the external `clrpro-azurefx` network and mounts the `clrpro-azurefx-token-files` volume read-only at the bridge's token-file path.

Use the same pattern for your own Compose services: attach them to `clrpro-azurefx`, mount `clrpro-azurefx-token-files` at `/var/opt/azcmagent/tokens`, and set the identity endpoint environment variables:

```yaml
environment:
  IDENTITY_ENDPOINT: http://clrpro-azurefx-bridge:40342/metadata/identity/oauth2/token
  IMDS_ENDPOINT: http://clrpro-azurefx-bridge:40342
volumes:
  - type: volume
    source: clrpro-azurefx-token-files
    target: /var/opt/azcmagent/tokens
    read_only: true
networks:
  - clrpro-azurefx
```

Declare the bridge resources as external in your Compose file:

```yaml
networks:
  clrpro-azurefx:
    external: true
volumes:
  clrpro-azurefx-token-files:
    external: true
```

The Azure permissions available to applications are those of the account signed in to Az PowerShell. A successful token request does not guarantee that the account is authorized for a particular resource.

## Security notes

Treat membership in `clrpro-azurefx` as trusted: containers on that network can reach the bridge. The short-lived token-file challenge is shared with clients that mount `clrpro-azurefx-token-files`; do not attach untrusted containers or publish the bridge endpoint to an untrusted network. The Az profile volume contains sensitive sign-in state. Do not share it or mount it into application containers.

## Automated end-to-end retest

Use a dedicated App Registration with a client secret. No Azure role assignments are required to test token acquisition. This tests service-principal sign-in through Azure PowerShell, not interactive developer sign-in or MFA.

The test reads credentials **only** from `e2e-credentials.json` at the checkout root, regardless of the current working directory. Credential environment variables are not used. The JSON object must have this shape (replace placeholders with your App Registration details):

```json
{
  "TenantId": "<directory-tenant-GUID>",
  "ClientId": "<application-client-GUID>",
  "ClientSecret": "<client-secret-value>"
}
```

Create the file using masked input rather than placing the secret in shell history:

```powershell
./test/set-credentials.ps1
./test/end-to-end.ps1
```

Alternatively, `./test/set-credentials.ps1 -RunTest` creates the file and immediately starts testing. The setup script refuses to overwrite an existing credentials file. On Linux/WSL it creates the file with owner-only read/write permissions; on Windows restrict its NTFS permissions to your account and trusted administrators.

The credentials file is excluded from Git and the Docker build context, and is preserved during every test wipe so the agent can debug and retry without new input. It contains a plaintext secret: do not share it, upload it, or copy it into images. Delete it explicitly when testing is finished. `.gitignore` does not protect a file that was already tracked; never force-add it.

The script wipes its dedicated Docker resources before each run, builds the bridge and demo, signs in through `Connect-AzAccount`, and requests a real token through the Compose client's `ManagedIdentityCredential`. It checks that the token is nonempty, unexpired, and has the expected tenant and application claims. Claim inspection is an identity assertion, not cryptographic token-signature verification.

It then recreates the bridge without signing in again and repeats token acquisition to verify the persisted Az profile. It also verifies that startup leaves a running container untouched and starts a stopped one.

**Destructive scope:** only the `clrpro-azurefx-e2e` Compose project, `clrpro-azurefx-e2e-bridge` container, `clrpro-azurefx-e2e` network, `clrpro-azurefx-e2e-az-profile` and `clrpro-azurefx-e2e-token-files` volumes, and the two `clrpro/azurefx-*:e2e` image tags are removed. Cleanup runs after success or failure. Regular sidecar resources, other Docker resources, and shared build caches are not pruned. Do not use these reserved test names for anything else.

The secret is passed to the sign-in process over standard input, not in Docker arguments, Compose configuration, or the bridge's environment. Application containers receive only the expected tenant/client IDs and the token-file volume; they cannot authenticate directly with the service-principal secret. The saved Az profile can contain sensitive sign-in state until cleanup removes its volume.

## Stop and remove the sidecar

Stop and remove the bridge container while keeping the account and token volumes:

```powershell
docker rm --force clrpro-azurefx-bridge
```

To sign out and remove the persisted Azure account profile:

```powershell
docker volume rm clrpro-azurefx-az-profile
```

Remove the token-exchange volume and network only after stopping any Compose clients that use them:

```powershell
docker volume rm clrpro-azurefx-token-files
docker network rm clrpro-azurefx
```
