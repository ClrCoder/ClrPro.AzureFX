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

If you change the script's resource-name parameters, update the matching external network, volume, and bridge hostname in your Compose file. To deploy updated bridge sources, use `-Rebuild`. The image is built successfully before the existing container is stopped and recreated; the named volumes (including the signed-in account) and network are preserved:

```powershell
./start-container.ps1 -Rebuild
```

Sign in from PowerShell inside the running sidecar:

```powershell
docker exec -it clrpro-azurefx-bridge pwsh
Connect-AzAccount -UseDeviceAuthentication
Get-AzContext
```

Complete the device-code sign-in in your browser. The Az account and token cache are stored in the `clrpro-azurefx-az-profile` volume mounted at `/root/.Azure`. To change the signed-in account, run `Disconnect-AzAccount` and sign in again from the container.

The sidecar explicitly uses `AzurePowerShellCredential`, so tokens come from the signed-in Azure PowerShell context rather than another credential in the default chain. It exposes the managed-identity token endpoint on port `40342` inside the Docker network and uses a short-lived file-token challenge. The token exchange uses a separate shared volume so clients can read the challenge file without receiving the Az profile volume.

## Bridge configuration and lifecycle

Configure options under `LocalCredentialBridge` in appsettings or with `LocalCredentialBridge__...` environment variables:

| Option | Default | Behavior |
|---|---|---|
| `CredentialSource` | `DefaultAzureCredential`; Docker image overrides to `AzurePowerShell` | Selects the credential source. Default mode retains the existing `AzureDefaults` configuration. |
| `UseFileTokenAuth` | `true` | Requires the Arc token-file challenge; keep enabled for the sidecar. |
| `LocalTokensPath` | `.LocalCredentialBridgeTokens` under the user's home directory | Absolute local challenge directory; Docker overrides to `/var/opt/azcmagent/tokens`. |
| `RemoteTokensPath` | `/var/opt/azcmagent/tokens` | Absolute path visible to clients. |
| `AuthTokenTimeoutSeconds` | `10` | Challenge validity, from 1 to 3600 seconds. |
| `MaxPendingChallenges` | `256` | Maximum pending files, including consumed/expired files awaiting deletion. |

Configuration is validated at startup and remains fixed for the process lifetime; restart the bridge to apply changes. Challenges are consumed atomically, so a secret cannot authorize multiple concurrent requests. A background task checks expired files every second; deletion failures are logged and retried. Shutdown deletes pending files, and startup removes leftover GUID-named `.key` files from the dedicated token directory. Other filenames are left alone. Do not share this directory between multiple running bridges or store unrelated GUID-named `.key` files there.

At capacity the endpoint returns HTTP 429 with `Retry-After: 1`. Challenge-file failures and unavailable credentials return 503, authentication failures return 502, and missing/blank resources return 400. Successful token responses include `token_type: "Bearer"` and use `Cache-Control: no-store`. Request cancellation is propagated to challenge creation and Azure token acquisition.

`GET /health` reports HTTP service health without requesting an Azure token. It does not prove the account is signed in or authorized; use the Compose demo for that.

Run deterministic protocol tests (no Azure account or Docker required):

```powershell
dotnet test AzureFX.sln --filter "Category!=Manual"
```

## Verify the Managed Identity flow

The included demo runs `examples/ManagedIdentityDemo/Program.cs` directly in a .NET 10 SDK container, without a `.csproj`. Its `#:package` directives select Azure.Identity 1.21.0 and Azure.Core 1.63.0. It requests an Azure Resource Manager token using `DefaultAzureCredential`. With no direct credentials supplied to the client container, this exercises the Azure SDK's managed identity challenge flow against the bridge.

An additional compatibility client runs `test/AzureCore163Demo/auth.cs` directly in a .NET 10 SDK container, without a `.csproj`. Its `#:package Azure.Core@1.63.0` directive selects **Azure.Core 1.63.0**, using the `ManagedIdentityCredential` now provided by Azure.Core itself (no Azure.Identity dependency). It checks token validity and the expected tenant/application identity:

```powershell
docker compose -f examples/docker-compose.yml run --build --rm azure-core-163-demo
```

Both clients print their loaded Azure SDK versions, never their tokens. The automated E2E test runs both clients during initial sign-in, after bridge recreation, and after stop/start.

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

It then recreates the bridge using `start-container.ps1 -Rebuild` without signing in again and repeats token acquisition to verify the persisted Az profile. It also verifies that startup leaves a running container untouched and starts a stopped one.

**Destructive scope:** only the `clrpro-azurefx-e2e` Compose project, `clrpro-azurefx-e2e-bridge` container, `clrpro-azurefx-e2e` network, `clrpro-azurefx-e2e-az-profile` and `clrpro-azurefx-e2e-token-files` volumes, and the three test image tags (`clrpro/azurefx-local-credential-bridge:e2e`, `clrpro/azurefx-managed-identity-demo:e2e`, `clrpro/azurefx-core163-demo:e2e`) are removed. Cleanup runs after success or failure. Regular sidecar resources, other Docker resources, and shared build caches are not pruned. Do not use these reserved test names for anything else.

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
