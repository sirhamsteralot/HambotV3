using HambotV2.Commands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HambotV2
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddJsonFile("appsecrets.json");
            builder.Services.AddDbContext<DatabaseContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")), ServiceLifetime.Transient, ServiceLifetime.Transient);
            builder.Services.AddHostedService<DiscordClient>();
            builder.Services.AddTransient<ICommandHandler, CommandHandler>();
            builder.Services.AddSingleton<ICommandService, CommandService>();

            var host = builder.Build();
            host.Run();
        }
    }
}