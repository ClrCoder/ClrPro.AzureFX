FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app
COPY Program.cs .
RUN dotnet build Program.cs --configuration Release
ENTRYPOINT ["dotnet", "run", "--file", "Program.cs", "--configuration", "Release", "--no-build"]
