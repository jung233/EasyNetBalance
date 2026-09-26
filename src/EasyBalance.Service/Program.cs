using EasyBalance.Service;
using EasyBalance.Service.Core;
using EasyBalance.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "EasyBalance");
builder.Services.AddSingleton<IAdapterProvider, NetworkAdapterProvider>();
builder.Services.AddSingleton<HealthCoordinator>();
builder.Services.AddSingleton<SingBoxCapabilitiesDetector>();
builder.Services.AddSingleton<SingBoxConfigGenerator>();
builder.Services.AddSingleton<ISingBoxControlClient, SingBoxControlClient>();
builder.Services.AddSingleton<SingBoxManager>();
builder.Services.AddSingleton<EasyBalanceRuntime>();
builder.Services.AddHostedService<RuntimeHostedService>();
builder.Services.AddHostedService<PipeServer>();
await builder.Build().RunAsync();
