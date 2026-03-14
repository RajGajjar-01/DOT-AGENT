using OpenAI;
using OpenAI.Chat;
using DotAgent.Models;

namespace DotAgent.Services;

public record TokenUsage(int PromptTokens, int CompletionTokens)
{
    public int Total => PromptTokens + CompletionTokens;
}

public record LlmProvider(string Name, string ApiKey, string Model, string Endpoint);

public class LlmService
{
    private ChatClient _client;
    private LlmProvider _activeProvider;
    private readonly List<LlmProvider> _providers = [];

    public string ModelName => _activeProvider.Model;
    public string ProviderName => _activeProvider.Name;
    public IReadOnlyList<LlmProvider> Providers => _providers;

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
        var knownProviders = new[]
        {
            ("ZHIPU",   "glm-4.7-flash",           "https://open.bigmodel.cn/api/paas/v4/"),
            ("GROQ",    "qwen-qwq-32b",             "https://api.groq.com/openai/v1/"),
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
        _activeProvider = provider;
        _client = CreateClient(provider);
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

    public async Task<(string Response, TokenUsage Usage)> CompleteAsync(
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

        var fullResponse = new System.Text.StringBuilder();
        TokenUsage usage = new(0, 0);

        await foreach (var update in _client
            .CompleteChatStreamingAsync(chatMessages, cancellationToken: ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    fullResponse.Append(part.Text);
                    onToken(part.Text);
                }
            }

            if (update.Usage is { } u)
            {
                usage = new TokenUsage(u.InputTokenCount, u.OutputTokenCount);
            }
        }

        return (fullResponse.ToString(), usage);
    }
}