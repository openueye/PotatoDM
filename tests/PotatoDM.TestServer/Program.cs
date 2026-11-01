using PotatoDM.TestServer;

var port = TestServerApp.DefaultPort;

for (var index = 0; index < args.Length - 1; index++)
{
    if (args[index] == "--port" && int.TryParse(args[index + 1], out var parsed))
    {
        port = parsed;
    }
}

var app = TestServerApp.Build(port);
await app.RunAsync();

public partial class Program
{
}
