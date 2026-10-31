namespace HambotV2.Llm
{
    /// <summary>One chat message; <see cref="Role"/> is "system", "user", or "assistant".</summary>
    internal sealed record LlmMessage(string Role, string Content);

    /// <summary>The model's reply plus its token usage, for the per-feature accounting.</summary>
    internal sealed record LlmResponse(string Content, int InputTokens, int OutputTokens);

    /// <summary>
    /// The single seam to the LLM: chat completions. Implementations throw on API errors —
    /// the gate owns failure handling, accounting, and the guardrails; features never call
    /// the client directly.
    /// </summary>
    internal interface ILlmClient
    {
        Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default);

        /// <summary>The provider's available model ids (for <c>!model list</c> and set-validation).</summary>
        Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>A failed model call (non-success status, unreadable body, transport error).</summary>
    internal sealed class LlmClientException : Exception
    {
        public LlmClientException(string message) : base(message) { }
    }
}
