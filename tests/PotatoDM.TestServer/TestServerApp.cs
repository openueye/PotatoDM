using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace PotatoDM.TestServer;

public static class TestServerApp
{
    public const int DefaultPort = 5099;

    public static WebApplication Build(int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton<TokenStore>();

        var app = builder.Build();
        DownloadEndpoints.Map(app);
        return app;
    }

    /// <summary>Returns the actual bound base address, useful when the port was 0.</summary>
    public static string GetBoundAddress(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.FirstOrDefault();

        return address ?? throw new InvalidOperationException("Test server has no bound address.");
    }
}
