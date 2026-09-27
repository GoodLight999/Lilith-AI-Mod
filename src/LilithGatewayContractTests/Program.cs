using LilithTextInjector;

static void Equal(string expected, string actual, string name)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new InvalidOperationException($"{name}: expected '{expected}', got '{actual}'.");
}

Equal("Responses", GatewayWire.NormalizeProtocol("response"), "normalize responses");
Equal("AnthropicMessages", GatewayWire.NormalizeProtocol("messages"), "normalize messages");
Equal("ChatCompletions", GatewayWire.NormalizeProtocol("anything-else"), "normalize chat");

Equal("CommandCode", GatewayWire.ResolvePreset("Command Code", "OpenRouter"), "provider alias overrides preset");
Equal("EXPLABS", GatewayWire.ResolvePreset("Gateway", "Experiential Labs"), "configured EXPLABS preset");
Equal("OpenRouter", GatewayWire.ResolvePreset("Gateway", ""), "default preset");

Equal(
    "https://api.commandcode.ai/provider/v1/responses",
    GatewayWire.ResolveEndpoint("CommandCode", "Responses", ""),
    "Command Code Responses endpoint");
Equal(
    "https://opencode.ai/zen/go/v1/messages",
    GatewayWire.ResolveEndpoint("OpenCodeGo", "AnthropicMessages", ""),
    "OpenCode Go Messages endpoint");
Equal(
    "https://openrouter.ai/api/v1/chat/completions",
    GatewayWire.ResolveEndpoint("OpenRouter", "ChatCompletions", ""),
    "OpenRouter Chat endpoint");
Equal(
    "http://localhost:1234/v1/chat/completions",
    GatewayWire.ResolveEndpoint("Custom", "ChatCompletions", " http://localhost:1234/v1/chat/completions "),
    "custom endpoint");

Equal(
    "hello",
    GatewayWire.ParseReply("ChatCompletions", """
    {"choices":[{"message":{"content":"hello"}}]}
    """),
    "chat string reply");

Equal(
    "hello world",
    GatewayWire.ParseReply("ChatCompletions", """
    {"choices":[{"message":{"content":[{"type":"text","text":"hello "},{"type":"text","text":"world"}]}}]}
    """),
    "chat array reply");

Equal(
    "response text",
    GatewayWire.ParseReply("Responses", """
    {"output_text":"response text"}
    """),
    "responses output_text");

Equal(
    "part one part two",
    GatewayWire.ParseReply("Responses", """
    {"output":[{"type":"message","content":[{"type":"output_text","text":"part one "},{"type":"output_text","text":"part two"}]}]}
    """),
    "responses output array");

Equal(
    "anthropic reply",
    GatewayWire.ParseReply("AnthropicMessages", """
    {"content":[{"type":"text","text":"anthropic reply"}]}
    """),
    "anthropic reply");

Console.WriteLine("Gateway contract tests passed.");
