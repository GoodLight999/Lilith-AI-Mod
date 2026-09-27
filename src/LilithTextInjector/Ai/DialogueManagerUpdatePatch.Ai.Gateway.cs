using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LilithTextInjector;

internal static partial class DialogueManagerUpdatePatch
{
    private static async Task RequestGatewayAsync(
        string systemInstruction,
        string userText,
        PoseContext poseContext,
        bool japaneseVoiceMode)
    {
        var model = Plugin.GatewayModel.Value.Trim();
        var key = Plugin.GatewayApiKey.Value.Trim();
        var protocol = NormalizeGatewayProtocol(Plugin.GatewayProtocol.Value);
        var preset = ResolveGatewayPreset();
        var endpoint = ResolveGatewayEndpoint(preset, protocol);

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("Gateway endpoint is empty.");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Gateway model is empty.");

        object payload = protocol switch
        {
            "Responses" => new
            {
                model,
                instructions = systemInstruction,
                input = BuildGatewayConversationMessages(),
                max_output_tokens = 1024
            },
            "AnthropicMessages" => new
            {
                model,
                system = systemInstruction,
                messages = BuildGatewayConversationMessages(),
                max_tokens = 1024
            },
            _ => new
            {
                model,
                messages = BuildOpenAiMessages(systemInstruction),
                max_tokens = 1024,
                temperature = 0.8
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        ApplyGatewayAuthentication(request, key);
        ApplyGatewayExtraHeaders(request);
        if (protocol == "AnthropicMessages" && !request.Headers.Contains("anthropic-version"))
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gateway/{protocol} HTTP {(int)response.StatusCode}: {responseBody}");

        using var document = JsonDocument.Parse(responseBody);
        var rawReply = protocol switch
        {
            "Responses" => ParseResponsesReply(document.RootElement),
            "AnthropicMessages" => ParseAnthropicMessagesReply(document.RootElement),
            _ => ParseChatCompletionsReply(document.RootElement)
        };

        Plugin.PluginLog.LogInfo($"Gateway completed: preset={preset}, protocol={protocol}, rawChars={rawReply.Length}.");
        CompleteAiReply(rawReply, userText, poseContext, japaneseVoiceMode);
    }

    private static string ResolveGatewayPreset()
    {
        var provider = Plugin.AiProvider.Value?.Trim() ?? string.Empty;
        if (string.Equals(provider, "OpenRouter", StringComparison.OrdinalIgnoreCase))
            return "OpenRouter";
        if (string.Equals(provider, "CommandCode", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Command Code", StringComparison.OrdinalIgnoreCase))
            return "CommandCode";
        if (string.Equals(provider, "EXPLABS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "ExperientialLabs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Experiential Labs", StringComparison.OrdinalIgnoreCase))
            return "EXPLABS";
        if (string.Equals(provider, "OpenCodeGo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "OpenCode Go", StringComparison.OrdinalIgnoreCase))
            return "OpenCodeGo";

        var preset = Plugin.GatewayPreset.Value?.Trim() ?? string.Empty;
        if (string.Equals(preset, "CommandCode", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset, "Command Code", StringComparison.OrdinalIgnoreCase))
            return "CommandCode";
        if (string.Equals(preset, "EXPLABS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset, "ExperientialLabs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset, "Experiential Labs", StringComparison.OrdinalIgnoreCase))
            return "EXPLABS";
        if (string.Equals(preset, "OpenCodeGo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset, "OpenCode Go", StringComparison.OrdinalIgnoreCase))
            return "OpenCodeGo";
        if (string.Equals(preset, "Custom", StringComparison.OrdinalIgnoreCase))
            return "Custom";
        return "OpenRouter";
    }

    private static string ResolveGatewayEndpoint(string preset, string protocol)
    {
        var suffix = protocol switch
        {
            "Responses" => "responses",
            "AnthropicMessages" => "messages",
            _ => "chat/completions"
        };

        return preset switch
        {
            "CommandCode" => $"https://api.commandcode.ai/provider/v1/{suffix}",
            "EXPLABS" => $"https://api.experientiallabs.ai/v1/{suffix}",
            "OpenCodeGo" => $"https://opencode.ai/zen/go/v1/{suffix}",
            "Custom" => Plugin.GatewayEndpoint.Value.Trim(),
            _ => $"https://openrouter.ai/api/v1/{suffix}"
        };
    }

    private static string NormalizeGatewayProtocol(string? protocol)
    {
        if (string.Equals(protocol, "Responses", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol, "Response", StringComparison.OrdinalIgnoreCase))
            return "Responses";
        if (string.Equals(protocol, "AnthropicMessages", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol, "Messages", StringComparison.OrdinalIgnoreCase))
            return "AnthropicMessages";
        return "ChatCompletions";
    }

    private static object[] BuildGatewayConversationMessages()
    {
        var messages = new List<object>();
        lock (MemoryLock)
        {
            foreach (var turn in RecentConversation)
                messages.Add(new { role = turn.Role == "model" ? "assistant" : "user", content = turn.Text });
        }
        return messages.ToArray();
    }

    private static void ApplyGatewayAuthentication(HttpRequestMessage request, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var header = Plugin.GatewayAuthHeader.Value.Trim();
        if (string.IsNullOrWhiteSpace(header))
            return;

        var scheme = Plugin.GatewayAuthScheme.Value.Trim();
        var value = string.IsNullOrWhiteSpace(scheme) ? key : $"{scheme} {key}";
        request.Headers.TryAddWithoutValidation(header, value);
    }

    private static void ApplyGatewayExtraHeaders(HttpRequestMessage request)
    {
        var json = Plugin.GatewayExtraHeadersJson.Value.Trim();
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("ExtraHeadersJson must be a JSON object.");

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(value))
                    request.Headers.TryAddWithoutValidation(property.Name, value);
            }
        }
        catch (Exception exception)
        {
            Plugin.PluginLog.LogWarning($"Gateway extra headers were ignored: {exception.Message}");
        }
    }

    private static string ParseChatCompletionsReply(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return string.Empty;
        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content))
            return string.Empty;
        return ExtractJsonText(content);
    }

    private static string ParseResponsesReply(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString() ?? string.Empty;

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    builder.Append(text.GetString());
            }
        }
        return builder.ToString();
    }

    private static string ParseAnthropicMessagesReply(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                builder.Append(text.GetString());
        }
        return builder.ToString();
    }

    private static string ExtractJsonText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
                builder.Append(text.GetString());
        }
        return builder.ToString();
    }
}
