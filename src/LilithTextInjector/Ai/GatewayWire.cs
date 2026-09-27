using System;
using System.Text;
using System.Text.Json;

namespace LilithTextInjector;

internal static class GatewayWire
{
    internal const string ChatCompletions = "ChatCompletions";
    internal const string Responses = "Responses";
    internal const string AnthropicMessages = "AnthropicMessages";

    internal static string NormalizeProtocol(string? protocol)
    {
        if (string.Equals(protocol, "Responses", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol, "Response", StringComparison.OrdinalIgnoreCase))
            return Responses;

        if (string.Equals(protocol, "AnthropicMessages", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol, "Messages", StringComparison.OrdinalIgnoreCase))
            return AnthropicMessages;

        return ChatCompletions;
    }

    internal static string ResolvePreset(string? provider, string? configuredPreset)
    {
        var providerValue = provider?.Trim() ?? string.Empty;
        if (string.Equals(providerValue, "OpenRouter", StringComparison.OrdinalIgnoreCase))
            return "OpenRouter";
        if (string.Equals(providerValue, "CommandCode", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerValue, "Command Code", StringComparison.OrdinalIgnoreCase))
            return "CommandCode";
        if (string.Equals(providerValue, "EXPLABS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerValue, "ExperientialLabs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerValue, "Experiential Labs", StringComparison.OrdinalIgnoreCase))
            return "EXPLABS";
        if (string.Equals(providerValue, "OpenCodeGo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerValue, "OpenCode Go", StringComparison.OrdinalIgnoreCase))
            return "OpenCodeGo";

        var preset = configuredPreset?.Trim() ?? string.Empty;
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

    internal static string ResolveEndpoint(string preset, string protocol, string? customEndpoint)
    {
        var suffix = NormalizeProtocol(protocol) switch
        {
            Responses => "responses",
            AnthropicMessages => "messages",
            _ => "chat/completions"
        };

        return preset switch
        {
            "CommandCode" => $"https://api.commandcode.ai/provider/v1/{suffix}",
            "EXPLABS" => $"https://api.experientiallabs.ai/v1/{suffix}",
            "OpenCodeGo" => $"https://opencode.ai/zen/go/v1/{suffix}",
            "Custom" => customEndpoint?.Trim() ?? string.Empty,
            _ => $"https://openrouter.ai/api/v1/{suffix}"
        };
    }

    internal static string ParseReply(string protocol, string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return NormalizeProtocol(protocol) switch
        {
            Responses => ParseResponsesReply(document.RootElement),
            AnthropicMessages => ParseAnthropicMessagesReply(document.RootElement),
            _ => ParseChatCompletionsReply(document.RootElement)
        };
    }

    private static string ParseChatCompletionsReply(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return string.Empty;

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content))
            return string.Empty;

        return ExtractJsonText(content);
    }

    private static string ParseResponsesReply(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString() ?? string.Empty;

        if (!root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                    builder.Append(text.GetString());
            }
        }

        return builder.ToString();
    }

    private static string ParseAnthropicMessagesReply(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
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
