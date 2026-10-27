using System.Text;
using System.Text.Json.Nodes;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;

namespace Tikai.Core.Tests.Runs;

public class RunOutputTests
{
    [Fact]
    public void Branch_names_use_the_random_end_of_the_run_id()
    {
        var a = Guid.Parse("019a2b3c-4d5e-7f00-8a1b-2c3d4e5f6071");
        var b = Guid.Parse("019a2b3c-4d5e-7f00-8a1b-2c3d00000000");

        Assert.Equal("tikai/HUB-42-4e5f6071", RunBranch.Name("HUB-42", a));
        Assert.NotEqual(RunBranch.Name("HUB-42", a), RunBranch.Name("HUB-42", b));
        Assert.Null(Core.Git.BranchName.ValidationError(RunBranch.Name("HUB-42", Guid.CreateVersion7())));
    }

    [Fact]
    public void The_ticket_document_has_title_description_and_criteria_with_ids()
    {
        var first = new AcceptanceCriterion { Id = Guid.NewGuid(), Position = 1, Text = "Second" };
        var second = new AcceptanceCriterion { Id = Guid.NewGuid(), Position = 0, Text = "First" };

        var text = TicketDocument.Render("HUB-7", "Add login", "Some *markdown*\r\nhere", [first, second]);

        Assert.StartsWith("# HUB-7: Add login\n", text, StringComparison.Ordinal);
        Assert.Contains("Some *markdown*\nhere\n", text, StringComparison.Ordinal);
        Assert.Contains("get_ticket", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("- First", StringComparison.Ordinal) < text.IndexOf("- Second", StringComparison.Ordinal));
        Assert.Contains($"(id `{second.Id:D}`)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ticket_without_criteria_says_so()
    {
        Assert.Contains("None yet.", TicketDocument.Render("HUB-1", "T", "", []), StringComparison.Ordinal);
    }

    [Fact]
    public void Long_strings_are_cut_everywhere_in_the_payload()
    {
        var payload = new JsonObject
        {
            ["text"] = new string('a', 5_000),
            ["nested"] = new JsonObject { ["list"] = new JsonArray("short", new string('b', 4_001)) },
            ["number"] = 42,
        };

        var trimmed = RunEventPayload.Trim(payload);

        Assert.Equal(new string('a', 4_000) + "… [1000 more characters]", (string?)trimmed["text"]);
        Assert.Equal("short", (string?)trimmed["nested"]!["list"]![0]);
        Assert.EndsWith("… [1 more characters]", (string?)trimmed["nested"]!["list"]![1], StringComparison.Ordinal);
        Assert.Equal(42, (int)trimmed["number"]!);
        Assert.Equal(5_000, ((string?)payload["text"])!.Length); // the input is left as it was
    }

    [Fact]
    public void A_payload_that_stays_too_big_becomes_a_preview()
    {
        var payload = new JsonObject { ["items"] = new JsonArray([.. Enumerable.Range(0, 20).Select(_ => (JsonNode?)new string('c', 3_000))]) };

        var trimmed = RunEventPayload.Trim(payload);

        Assert.True((bool)trimmed["truncated"]!);
        Assert.True(Encoding.UTF8.GetByteCount(trimmed.ToJsonString()) <= RunEventPayload.MaxPayloadBytes);
    }

    [Fact]
    public void The_run_secrets_and_anything_that_looks_like_a_key_or_token_are_redacted()
    {
        var redactor = new SecretRedactor(["my-provider-key-1234", null, "short"]);

        var text = redactor.Redact(
            "key my-provider-key-1234 and sk-ant-api03-AbC_d-1234567890 and tikai_run_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA short");

        Assert.Equal("key [redacted] and [redacted] and [redacted] short", text);
    }

    [Fact]
    public void Ordinary_text_is_left_alone()
    {
        var text = "sk-ant- is a prefix; tikai_run_ too; a normal line.";

        Assert.Equal(text, new SecretRedactor([]).Redact(text));
    }
}
