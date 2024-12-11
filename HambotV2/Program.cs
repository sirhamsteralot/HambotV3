using HambotV2.Connections;

namespace HambotV2
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddJsonFile("appsecrets.json");
            builder.Services.AddHostedService<DiscordClient>();

            var host = builder.Build();
            host.Run();
        }
    }
}