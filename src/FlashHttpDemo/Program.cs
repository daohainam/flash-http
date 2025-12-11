using FlashHttp.Server;

var serverBuilder = new FlashHttpServerBuilder().WithPort(8080);
var server = serverBuilder.Build();

server.WithGetHandler("/", (request, response) =>
{
    //response.StatusCode = 200;
    //response.Headers["Content-Type"] = "text/plain";
    //response.Body = System.Text.Encoding.UTF8.GetBytes("Hello, FlashHttp!");
});

await server.StartAsync();
