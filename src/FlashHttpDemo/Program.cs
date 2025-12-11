using FlashHttp.Server;
using FlashHttpDemo;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new FlashHttpServerOptions() { 
    Port = 8080 
});
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

