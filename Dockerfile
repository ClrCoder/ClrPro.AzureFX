FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet publish src/ClrPro.AzureFX.LocalCredentialBridge/ClrPro.AzureFX.LocalCredentialBridge.csproj \
    --configuration Release \
    --output /app/publish \
    --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV DEBIAN_FRONTEND=noninteractive
WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates wget \
    && os_id="$(. /etc/os-release && echo "$ID")" \
    && os_version="$(. /etc/os-release && echo "$VERSION_ID")" \
    && wget --quiet "https://packages.microsoft.com/config/$os_id/$os_version/packages-microsoft-prod.deb" \
        --output-document=/tmp/packages-microsoft-prod.deb \
    && dpkg --install /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install --yes --no-install-recommends powershell \
    && rm -rf /var/lib/apt/lists/*

RUN pwsh -NoLogo -NoProfile -Command \
    '$ErrorActionPreference = "Stop"; Set-PSRepository -Name PSGallery -InstallationPolicy Trusted; Install-Module -Name Az -Repository PSGallery -Scope AllUsers -Force -AllowClobber'

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:40342 \
    LocalCredentialBridge__LocalTokensPath=/var/opt/azcmagent/tokens \
    LocalCredentialBridge__RemoteTokensPath=/var/opt/azcmagent/tokens

USER root
EXPOSE 40342
ENTRYPOINT ["dotnet", "ClrPro.AzureFX.LocalCredentialBridge.dll"]
