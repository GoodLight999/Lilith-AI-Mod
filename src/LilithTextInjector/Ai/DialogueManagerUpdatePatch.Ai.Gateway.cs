using System;
using System.Collections.Generic;
using System.Net.Http;
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
        var protocol = GatewayWire.NormalizeProtocol(Plugin.GatewayProtocol.Value);
        var preset = GatewayWire.ResolvePreset(Plugin.AiProvider.Value, Plugin.GatewayPreset.Value);
        var endpoint = GatewayWire.ResolveEndpoint(preset, protocol, Plugin.GatewayEndpoint.Value);

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("Gateway endpoint is empty.");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Gateway model is empty.");

        object payload = protocol switch
        {
            GatewayWire.Responses => new
            {
                model,
                instructions = systemInstruction,
                input = BuildGatewayConversationMessages(),
                max_output_tokens = 1024
            },
            GatewayWire.AnthropicMessages => new
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
        if (protocol == GatewayWire.AnthropicMessages && !request.Headers.Contains("anthropic-version"))
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gateway/{protocol} HTTP {(int)response.StatusCode}: {responseBody}");

        var rawReply = GatewayWire.ParseReply(protocol, responseBody);

        Plugin.PluginLog.LogInfo($"Gateway completed: preset={preset}, protocol={protocol}, rawChars={rawReply.Length}.");
        CompleteAiReply(rawReply, userText, poseContext, japaneseVoiceMode);
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

}
