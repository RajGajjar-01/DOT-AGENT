using OpenAI;
using OpenAI.Chat;
using DotAgent.Models;

namespace DotAgent.Services;

public record TokenUsage(int PromptTokens, int CompletionTokens)
{
    public int Total => PromptTokens + CompletionTokens;
}

public class LlmService
{
    private readonly ChatClient _client;
    private readonly string _model;

    public LlmService()
    {
        var apiKey = Environment.GetEnvironmentVariable("ZAI_API_KEY")
            ?? throw new InvalidOperationException(
                "ZAI_API_KEY not set. Add it to your .env file.");

        _model = Environment.GetEnvironmentVariable("ZAI_MODEL")
            ?? "glm-4.7-flash";

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://open.bigmodel.cn/api/paas/v4/")
        };

        var credential = new System.ClientModel.ApiKeyCredential(apiKey);
        var openAiClient = new OpenAIClient(credential, options);
        _client = openAiClient.GetChatClient(_model);
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