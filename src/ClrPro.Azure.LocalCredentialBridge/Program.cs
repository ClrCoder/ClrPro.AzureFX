// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Options;

[assembly: CLSCompliant(false)]

const string AzureDefaultsOptionsPath = "AzureDefaults";

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Azure SDK don't apply "ClientOptions defaults" to TokenCredentialOptions.
// We will allow to configure "DefaultAzureCredentialOptions" through IConfiguration, but will not apply "ClientOptions defaults".
builder.Services.Configure<DefaultAzureCredentialOptions>(o => o.ExcludeManagedIdentityCredential = true);
builder.Services.Configure<DefaultAzureCredentialOptions>(
    builder.Configuration.GetSection($"{AzureDefaultsOptionsPath}:DefaultAzureCredential"));

// This is so painfully difficult and hackish.
// In the Microsoft.Extensions.Azure the are no direct ways to obtain the same TokenCredentials as all clients uses.
// This approach more or less equivalent. Two times we trying to create TokenCredentials from IConfiguration (see ClientFactory.CreateCredential(configuration))
// and then falling back to the factory registered with "UseCredential".
builder.Services.AddTransient(
    sp => sp.GetRequiredService<AzureComponentFactory>()
        .CreateTokenCredential(builder.Configuration.GetSection(AzureDefaultsOptionsPath)));

// This is good thing, but it doesn't have customization of the DefaultAzureCredential.
// AddAzureClients subsystem is fairly complex.
builder.Services.AddAzureClients(
    azClients =>
    {
        azClients.UseCredential(
            sp => new DefaultAzureCredential(
                sp.GetRequiredService<IOptionsMonitor<DefaultAzureCredentialOptions>>().CurrentValue));

        // Here the "ClientOptions defaults" are registered
        azClients.ConfigureDefaults(builder.Configuration.GetSection(AzureDefaultsOptionsPath));
    });

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();

app.MapControllers();

app.Run();
