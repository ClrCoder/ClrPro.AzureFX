FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ManagedIdentityDemo.csproj .
RUN dotnet restore

COPY Program.cs .
RUN dotnet publish --configuration Release --output /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ManagedIdentityDemo.dll"]
