using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HambotV2.Llm
{
    /// <summary>
    /// The Mistral chat-completions implementation over a shared <see cref="HttpClient"/>.
    /// Registered only when an API key is configured; every failure surfaces as a
    /// <see cref="LlmClientException"/> for the gate to handle.
    /// </summary>
    internal sealed class MistralLmClient : ILlmClient
    {
        internal const string Endpoint = "https://api.mistral.ai/v1/chat/completions";
        internal const string ModelsEndpoint = "https://api.mistral.ai/v1/models";

        private static readonly HttpClient Client = new();
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly string _apiKey;
        private readonly ILlmModelResolver _modelResolver;

        public MistralLmClient(string apiKey, ILlmModelResolver modelResolver)
        {
            _apiKey = apiKey;
            _modelResolver = modelResolver;
        }

        public async Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
        {
            // The model is resolved per call so a runtime override (!model) applies without a restart.
            var payload = new
            {
                model = _modelResolver.Resolve(),
                messages = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray()
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new LlmClientException($"Mistral API returned {(int)response.StatusCode}: {body}");

            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
            JsonElement usage = root.GetProperty("usage");
            return new LlmResponse(content, usage.GetProperty("prompt_tokens").GetInt32(), usage.GetProperty("completion_tokens").GetInt32());
        }

        public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new LlmClientException($"Mistral API returned {(int)response.StatusCode}: {body}");

            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("data")
                .EnumerateArray()
                .Select(element => element.GetProperty("id").GetString() ?? string.Empty)
                .Where(id => id.Length > 0)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }
    }
}
