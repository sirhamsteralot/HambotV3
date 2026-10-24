using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace HambotV2
{
    /// <summary>
    /// Startup validation of required configuration keys (roadmap T4). Failing fast with an explicit
    /// message beats discovering a missing token or connection string at login/connection time.
    /// </summary>
    internal static class ConfigurationValidator
    {
        public static List<string> GetMissingKeys(IConfiguration configuration)
        {
            var missing = new List<string>();

            if (string.IsNullOrWhiteSpace(configuration["Secrets:BotToken"]))
                missing.Add("Secrets:BotToken");

            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
                missing.Add("ConnectionStrings:DefaultConnection");

            if (string.IsNullOrWhiteSpace(configuration["BotConfiguration:BotName"]))
                missing.Add("BotConfiguration:BotName");

            if (string.IsNullOrWhiteSpace(configuration["BotConfiguration:LogFormat"]))
                missing.Add("BotConfiguration:LogFormat");

            if (string.IsNullOrWhiteSpace(configuration["BotConfiguration:CommandIdentifier"]))
                missing.Add("BotConfiguration:CommandIdentifier");

            return missing;
        }
    }
}
