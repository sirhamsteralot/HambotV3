using HambotV2.Connections;

namespace HambotV2.Llm
{
    /// <summary>
    /// The global runtime LLM overrides persisted in <c>BotSetting</c> and their resolution:
    /// the effective model is the runtime override (<c>!model</c>) → the <c>Llm:Model</c>
    /// configuration value → the shipped default.
    /// </summary>
    internal static class LlmRuntimeSettings
    {
        /// <summary>The <c>BotSetting</c> key holding the global runtime model override.</summary>
        public const string ModelKey = "LlmModel";

        public static string ResolveModel(IDatabaseContext? database, LlmSettings settings)
        {
            return ResolveModelWithLayer(database, settings).Model;
        }

        /// <summary>Resolves the effective model and reports which layer supplied it.</summary>
        public static (string Model, string Layer) ResolveModelWithLayer(IDatabaseContext? database, LlmSettings settings)
        {
            string? runtimeOverride = database?.BotSettings.FirstOrDefault(setting => setting.Key == ModelKey)?.Value;
            if (!string.IsNullOrWhiteSpace(runtimeOverride))
                return (runtimeOverride, "runtime override (!model)");

            if (!string.IsNullOrWhiteSpace(settings.Model) && settings.Model != LlmSettings.DefaultModel)
                return (settings.Model, "Llm:Model configuration");

            return (LlmSettings.DefaultModel, "shipped default");
        }
    }

    /// <summary>Resolves the effective LLM model for each model call (runtime override wins).</summary>
    internal interface ILlmModelResolver
    {
        string Resolve();
    }

    internal sealed class LlmModelResolver : ILlmModelResolver
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly LlmSettings _settings;

        public LlmModelResolver(IServiceScopeFactory scopeFactory, LlmSettings settings)
        {
            _scopeFactory = scopeFactory;
            _settings = settings;
        }

        public string Resolve()
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
            return LlmRuntimeSettings.ResolveModel(database, _settings);
        }
    }
}
