FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app
COPY test/AzureCore163Demo/auth.cs .
RUN dotnet build auth.cs --configuration Release
ENTRYPOINT ["dotnet", "run", "--file", "auth.cs", "--configuration", "Release", "--no-build"]
