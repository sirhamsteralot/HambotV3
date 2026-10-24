using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace BotTests
{
    [TestClass]
    public sealed class TestConfigurationValidator
    {
        private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }

        private static Dictionary<string, string?> CompleteConfiguration()
        {
            return new Dictionary<string, string?>
            {
                { "Secrets:BotToken", "some-token" },
                { "ConnectionStrings:DefaultConnection", "Server=localhost;Database=hambotdb;" },
                { "BotConfiguration:BotName", "HamBot" },
                { "BotConfiguration:LogFormat", "{0}: {1}" },
                { "BotConfiguration:CommandIdentifier", "!" },
            };
        }

        [TestMethod]
        public void Complete_Configuration_Has_No_Missing_Keys()
        {
            var configuration = BuildConfiguration(CompleteConfiguration());

            Assert.AreEqual(0, HambotV2.ConfigurationValidator.GetMissingKeys(configuration).Count);
        }

        [TestMethod]
        public void Missing_BotToken_Is_Reported_By_Key()
        {
            var values = CompleteConfiguration();
            values.Remove("Secrets:BotToken");

            var missing = HambotV2.ConfigurationValidator.GetMissingKeys(BuildConfiguration(values));

            CollectionAssert.AreEqual(new List<string> { "Secrets:BotToken" }, missing);
        }

        [TestMethod]
        public void Missing_ConnectionString_Is_Reported_By_Key()
        {
            var values = CompleteConfiguration();
            values.Remove("ConnectionStrings:DefaultConnection");

            var missing = HambotV2.ConfigurationValidator.GetMissingKeys(BuildConfiguration(values));

            CollectionAssert.AreEqual(new List<string> { "ConnectionStrings:DefaultConnection" }, missing);
        }

        [TestMethod]
        public void Missing_BotConfiguration_Section_Reports_All_Keys()
        {
            var values = CompleteConfiguration();
            values.Remove("BotConfiguration:BotName");
            values.Remove("BotConfiguration:LogFormat");
            values.Remove("BotConfiguration:CommandIdentifier");

            var missing = HambotV2.ConfigurationValidator.GetMissingKeys(BuildConfiguration(values));

            CollectionAssert.AreEqual(new List<string>
            {
                "BotConfiguration:BotName",
                "BotConfiguration:LogFormat",
                "BotConfiguration:CommandIdentifier"
            }, missing);
        }

        [TestMethod]
        public void Empty_Configuration_Reports_All_Keys()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>());

            var missing = HambotV2.ConfigurationValidator.GetMissingKeys(configuration);

            Assert.AreEqual(5, missing.Count);
            CollectionAssert.Contains(missing, "Secrets:BotToken");
            CollectionAssert.Contains(missing, "ConnectionStrings:DefaultConnection");
        }

        [TestMethod]
        public void Whitespace_Only_Values_Are_Treated_As_Missing()
        {
            var values = CompleteConfiguration();
            values["Secrets:BotToken"] = "   ";

            var missing = HambotV2.ConfigurationValidator.GetMissingKeys(BuildConfiguration(values));

            CollectionAssert.AreEqual(new List<string> { "Secrets:BotToken" }, missing);
        }

        [TestMethod]
        public void Environment_Variable_Provider_Resolves_Configuration_Keys()
        {
            // Containers supply configuration via environment variables ("__" maps to the ":" key
            // separator). This verifies the validator's key names work with that provider.
            Environment.SetEnvironmentVariable("Secrets__BotToken", "token");
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "Server=db;");
            try
            {
                var configuration = new ConfigurationBuilder()
                    .AddEnvironmentVariables()
                    .Build();

                Assert.AreEqual("token", configuration["Secrets:BotToken"]);
                Assert.AreEqual("Server=db;", configuration.GetConnectionString("DefaultConnection"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("Secrets__BotToken", null);
                Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
            }
        }
    }
}
