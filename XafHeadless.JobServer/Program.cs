namespace XafHeadless.JobServer;

public class Program {
    public static void Main(string[] args) => CreateHostBuilder(args).Build().Run();

    // XAF's Startup pattern (ConfigureServices/Configure) requires the Generic Host + UseStartup,
    // not the minimal-hosting WebApplication builder. Mirrors XafHeadless.Api/Program.cs — plain
    // ILogger, no Serilog (the Api does not use Serilog either).
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder => webBuilder
                .UseStartup<Startup>()
                .UseUrls("http://localhost:5300"));
}
