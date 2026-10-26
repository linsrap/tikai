using Tikai.Core.Llm;

namespace Tikai.Core.Tests.Llm;

public class LlmProviderRulesTests
{
    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-haiku-5-5")]
    [InlineData("llama3.1:8b")]
    public void Valid_models_pass(string model)
    {
        Assert.Null(LlmProviderRules.ModelError(model));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Claude Opus")]
    [InlineData("-model")]
    [InlineData("claude/opus")]
    public void Invalid_models_are_rejected(string? model)
    {
        Assert.NotNull(LlmProviderRules.ModelError(model));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://api.anthropic.com")]
    [InlineData("https://gateway.example.org/anthropic")]
    public void Valid_base_urls_pass(string? url)
    {
        Assert.Null(LlmProviderRules.BaseUrlError(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://api.anthropic.com")]
    [InlineData("https://user:pw@gateway.example.org")]
    [InlineData("https://gateway.example.org/?key=1")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public void Invalid_base_urls_are_rejected(string url)
    {
        Assert.NotNull(LlmProviderRules.BaseUrlError(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("sk-ant-api03 with space")]
    [InlineData("sk-ant-api03-abc\ndef")]
    public void Invalid_keys_are_rejected(string? key)
    {
        Assert.NotNull(LlmProviderRules.ApiKeyError(key));
    }

    [Fact]
    public void The_key_hint_is_the_last_four_characters_and_empty_for_short_keys()
    {
        Assert.Equal("WxYz", LlmProviderRules.KeyHint(" sk-ant-api03-abcdefghijWxYz "));
        Assert.Equal("", LlmProviderRules.KeyHint("12345678"));
        Assert.Null(LlmProviderRules.ApiKeyError("sk-ant-api03-abcdefghijWxYz"));
    }

    [Fact]
    public void A_connection_never_prints_its_key()
    {
        var connection = new LlmConnection(LlmProviderKind.Anthropic, "sk-ant-secret", "claude-opus-5-5", null);

        Assert.DoesNotContain("sk-ant-secret", connection.ToString(), StringComparison.Ordinal);
        Assert.Contains("claude-opus-5-5", connection.ToString(), StringComparison.Ordinal);
    }
}
