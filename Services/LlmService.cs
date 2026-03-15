using OpenAI;
using OpenAI.Chat;
using DotAgent.Models;

namespace DotAgent.Services;

public record TokenUsage(int PromptTokens, int CompletionTokens, int? CachedTokens = null)
{
    public int Total => PromptTokens + CompletionTokens;
}

public record LlmMetrics(
    string Provider,
    string Model,
    double TimeToFirstTokenMs,
    double TotalDurationMs,
    TokenUsage Usage);

public record LlmProvider(string Name, string ApiKey, string Model, string Endpoint);

public class LlmService
{
    private ChatClient _client;
    private LlmProvider _activeProvider;
    private readonly List<LlmProvider> _providers = [];
    private int _currentProviderIndex = 0;

    public string ModelName => _activeProvider.Model;
    public string ProviderName => _activeProvider.Name;
    public IReadOnlyList<LlmProvider> Providers => _providers;
    public event Action<string, LlmProvider, LlmProvider>? OnProviderSwitched;

    public LlmService()
    {
        LoadProviders();

        if (_providers.Count == 0)
            throw new InvalidOperationException(
                "No LLM providers configured. Add at least one provider to your .env file.\n" +
                "Example: ZHIPU_API_KEY=..., ZHIPU_MODEL=glm-4.7-flash, ZHIPU_ENDPOINT=https://open.bigmodel.cn/api/paas/v4/");

        _activeProvider = _providers[0];
        _client = CreateClient(_activeProvider);
    }

    private void LoadProviders()
    {
        // Each provider is defined by: {NAME}_API_KEY, {NAME}_MODEL, {NAME}_ENDPOINT
        // Groq is listed first for fastest inference (specialized hardware)
        var knownProviders = new[]
        {
            ("GROQ",    "llama-3.3-70b-versatile",  "https://api.groq.com/openai/v1/"),
            ("ZHIPU",   "glm-4.7-flash",            "https://open.bigmodel.cn/api/paas/v4/"),
        };

        foreach (var (prefix, defaultModel, defaultEndpoint) in knownProviders)
        {
            var apiKey = Environment.GetEnvironmentVariable($"{prefix}_API_KEY");

            // ZAI_ is an alias for ZHIPU_
            if (string.IsNullOrWhiteSpace(apiKey) && prefix == "ZHIPU")
                apiKey = Environment.GetEnvironmentVariable("ZAI_API_KEY");

            if (string.IsNullOrWhiteSpace(apiKey)) continue;

            var model = Environment.GetEnvironmentVariable($"{prefix}_MODEL")
                ?? (prefix == "ZHIPU" ? Environment.GetEnvironmentVariable("ZAI_MODEL") : null)
                ?? defaultModel;
            var endpoint = Environment.GetEnvironmentVariable($"{prefix}_ENDPOINT") ?? defaultEndpoint;

            _providers.Add(new LlmProvider(prefix, apiKey, model, endpoint));
        }
    }

    public void SwitchProvider(LlmProvider provider)
    {
        var previous = _activeProvider;
        _activeProvider = provider;
        _client = CreateClient(provider);
        _currentProviderIndex = _providers.IndexOf(provider);
        OnProviderSwitched?.Invoke("Manual switch", previous, provider);
    }

    /// <summary>
    /// Switch to the next available provider. Returns true if switched, false if no more providers.
    /// </summary>
    public bool SwitchToNextProvider(string reason)
    {
        var previous = _activeProvider;
        var nextIndex = (_currentProviderIndex + 1) % _providers.Count;
        
        // If we've cycled through all providers, return false
        if (nextIndex == _currentProviderIndex)
            return false;
        
        _currentProviderIndex = nextIndex;
        _activeProvider = _providers[nextIndex];
        _client = CreateClient(_activeProvider);
        OnProviderSwitched?.Invoke(reason, previous, _activeProvider);
        return true;
    }

    /// <summary>
    /// Check if an error indicates rate limit, quota exceeded, or API key issue.
    /// </summary>
    public static bool IsRateLimitError(Exception ex)
    {
        var msg = ex.Message.ToLowerInvariant();
        return msg.Contains("rate limit") ||
               msg.Contains("quota") ||
               msg.Contains("exceeded") ||
               msg.Contains("429") ||
               msg.Contains("insufficient") ||
               msg.Contains("billing") ||
               msg.Contains("credits") ||
               msg.Contains("unauthorized") ||
               msg.Contains("invalid api key") ||
               msg.Contains("api key");
    }

    private static ChatClient CreateClient(LlmProvider provider)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Endpoint)
        };
        var credential = new System.ClientModel.ApiKeyCredential(provider.ApiKey);
        var openAiClient = new OpenAIClient(credential, options);
        return openAiClient.GetChatClient(provider.Model);
    }

    public async Task<(string Response, TokenUsage Usage, LlmMetrics Metrics)> CompleteAsync(
        IEnumerable<Message> history,
        string systemPrompt,
        Action<string> onToken,
        CancellationToken ct = default)
    {
        var chatMessages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(systemPrompt)
        };

        foreach (var m in history)
        {
            chatMessages.Add(m.Role switch
            {
                "user"        => ChatMessage.CreateUserMessage(m.Content),
                "assistant"   => ChatMessage.CreateAssistantMessage(m.Content),
                "tool_result" => ChatMessage.CreateUserMessage(m.Content),
                _             => ChatMessage.CreateUserMessage(m.Content)
            });
        }

        // Try with automatic fallback on rate limit errors
        int attempts = 0;
        int maxAttempts = _providers.Count;
        Exception? lastError = null;

        while (attempts < maxAttempts)
        {
            attempts++;
            var fullResponse = new System.Text.StringBuilder();
            TokenUsage usage = new(0, 0);
            
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            double ttftMs = 0;
            bool firstToken = true;

            try
            {
                // Create a linked CTS with timeout to prevent indefinite hang
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                
                await foreach (var update in _client
                    .CompleteChatStreamingAsync(chatMessages, cancellationToken: linkedCts.Token))
                {
                    try
                    {
                        foreach (var part in update.ContentUpdate)
                        {
                            if (!string.IsNullOrEmpty(part.Text))
                            {
                                if (firstToken)
                                {
                                    ttftMs = totalSw.Elapsed.TotalMilliseconds;
                                    firstToken = false;
                                }
                                fullResponse.Append(part.Text);
                                onToken(part.Text);
                            }
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("ChatFinishReason") || ex.Message.Contains("finish_reason"))
                    {
                        // Non-standard finish_reason from provider (e.g., "network_error" from GROQ/ZHIPU)
                        // We've already collected the response content, so we can continue
                        // Just log and break out of this update
                        if (fullResponse.Length > 0)
                        {
                            // We have content, return what we got
                            break;
                        }
                        throw; // No content, re-throw to trigger fallback
                    }

                    if (update.Usage is { } u)
                    {
                        // Try to get cached tokens if available (OpenAI-specific)
                        int? cached = null;
                        // Note: OpenAI SDK may expose cached_tokens via u.AdditionalProperties or similar
                        // For now, we keep it null and can extend later
                        usage = new TokenUsage(u.InputTokenCount, u.OutputTokenCount, cached);
                    }
                }

                totalSw.Stop();
                var metrics = new LlmMetrics(
                    _activeProvider.Name,
                    _activeProvider.Model,
                    ttftMs,
                    totalSw.Elapsed.TotalMilliseconds,
                    usage);

                return (fullResponse.ToString(), usage, metrics);
            }
            catch (Exception ex) when (IsRateLimitError(ex))
            {
                lastError = ex;
                
                // Try to switch to next provider
                if (SwitchToNextProvider($"Rate limit/API error: {ex.Message.Split('\n')[0]}"))
                {
                    onToken($"\n[System: Switching to {_activeProvider.Name} ({_activeProvider.Model}) due to API error...]\n");
                    continue;
                }
                
                // No more providers to try
                throw new InvalidOperationException(
                    $"All providers exhausted. Last error: {ex.Message}", ex);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ChatFinishReason") || ex.Message.Contains("finish_reason") || ex.Message.Contains("network_error"))
            {
                lastError = ex;
                
                // Provider returned non-standard finish_reason
                if (fullResponse.Length > 0)
                {
                    // We have partial content, return it
                    totalSw.Stop();
                    var metrics = new LlmMetrics(
                        _activeProvider.Name,
                        _activeProvider.Model,
                        ttftMs,
                        totalSw.Elapsed.TotalMilliseconds,
                        usage);
                    return (fullResponse.ToString(), usage, metrics);
                }
                
                // No content, try next provider
                if (SwitchToNextProvider($"Provider returned non-standard response: {ex.Message.Split('\n')[0]}"))
                {
                    onToken($"\n[System: Switching to {_activeProvider.Name} ({_activeProvider.Model}) due to response error...]\n");
                    continue;
                }
                
                throw new InvalidOperationException(
                    $"All providers exhausted. Last error: {ex.Message}", ex);
            }
        }

        throw new InvalidOperationException(
            $"All providers exhausted after {attempts} attempts. Last error: {lastError?.Message}", lastError!);
    }
}