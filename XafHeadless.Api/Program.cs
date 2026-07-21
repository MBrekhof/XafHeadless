namespace XafHeadless.Api;

public class Program {
    public static void Main(string[] args) => CreateHostBuilder(args).Build().Run();

    // XAF's Startup pattern (ConfigureServices/Configure) requires the Generic Host + UseStartup,
    // not the minimal-hosting WebApplication builder that `dotnet new web` scaffolds.
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder => webBuilder
                .UseStartup<Startup>()
                .UseUrls("http://localhost:5200"));
}
