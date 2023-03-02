# Local Credentials Bridge

## Using as a Dotnet Tool

Luckily, WebApis can also be packaged as dotnet tools and ran. To do this simple use the following commands from the solution root.

`dotnet pack`

`dotnet tool update -g --add-source ./pack ClrPro.AzureFX.LocalCredentialBridge`

Then run with the following command:

`az-credentials-bridge`