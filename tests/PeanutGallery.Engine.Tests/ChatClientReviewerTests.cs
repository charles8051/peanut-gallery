using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class ChatClientReviewerTests
{
	private static readonly ProviderConfig OpenRouter =
		new("openrouter", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY");

	private static ReviewTask TaskFor(string provider = "openrouter", double? topP = null, int? topK = null)
	{
		var persona = new Persona(
			"architect", "The Architect", "architecture", ReviewTier.Diff,
			new ModelRef(provider, "some/model"), 0.2, "system prompt", TopP: topP, TopK: topK);
		var repo = new RepoTarget("demo", "/tmp/demo");
		var request = PromptAssembly.Build(persona, repo, Diff.Empty);
		return new ReviewTask(persona, repo, request);
	}

	[Fact]
	public async Task Missing_api_key_becomes_a_failure_finding_not_a_throw()
	{
		// env resolver returns null for every variable -> no key available.
		var reviewer = new ChatClientReviewer([OpenRouter], env: _ => null);

		var review = await reviewer.ReviewAsync(TaskFor());

		var finding = Assert.Single(review.Findings);
		Assert.Equal(Severity.Major, finding.Severity);
		Assert.Contains("OPENROUTER_API_KEY", finding.Body);
	}

	[Fact]
	public async Task Unknown_provider_becomes_a_failure_finding()
	{
		var reviewer = new ChatClientReviewer([OpenRouter], env: _ => "key");

		var review = await reviewer.ReviewAsync(TaskFor(provider: "nonexistent"));

		var finding = Assert.Single(review.Findings);
		Assert.Equal(Severity.Major, finding.Severity);
		Assert.Contains("no provider", finding.Body);
	}

	[Fact]
	public async Task It_puts_the_output_cap_and_persona_temperature_on_the_request()
	{
		// The whole point of the cap is that it reaches the wire. Inject a fake client that captures
		// the ChatOptions the reviewer builds, so a regression that dropped MaxOutputTokens (or the
		// temperature) is caught here instead of only in a live runaway.
		Microsoft.Extensions.AI.ChatOptions? captured = null;
		var reviewer = new ChatClientReviewer(
			[OpenRouter], env: _ => "key", maxOutputTokens: 12345,
			clientFactory: (_, _, _, _) => new CapturingClient(o => captured = o));

		await reviewer.ReviewAsync(TaskFor()); // persona temperature is 0.2

		Assert.NotNull(captured);
		Assert.Equal(12345, captured!.MaxOutputTokens);
		Assert.Equal(0.2f, captured.Temperature);
	}

	[Fact]
	public async Task It_puts_top_p_and_top_k_on_the_request_when_the_persona_sets_them()
	{
		// The recommended MiniMax-M3 sampling (top_p 0.95, top_k 40) only helps if it reaches the wire.
		Microsoft.Extensions.AI.ChatOptions? captured = null;
		var reviewer = new ChatClientReviewer(
			[OpenRouter], env: _ => "key",
			clientFactory: (_, _, _, _) => new CapturingClient(o => captured = o));

		await reviewer.ReviewAsync(TaskFor(topP: 0.95, topK: 40));

		Assert.Equal(0.95f, captured!.TopP);
		Assert.Equal(40, captured.TopK);
	}

	[Fact]
	public async Task It_leaves_top_p_and_top_k_unset_when_the_persona_omits_them()
	{
		// Absent on the persona -> absent on the wire, so the provider default stands (not a forced 0).
		Microsoft.Extensions.AI.ChatOptions? captured = null;
		var reviewer = new ChatClientReviewer(
			[OpenRouter], env: _ => "key",
			clientFactory: (_, _, _, _) => new CapturingClient(o => captured = o));

		await reviewer.ReviewAsync(TaskFor());

		Assert.Null(captured!.TopP);
		Assert.Null(captured.TopK);
	}

	[Fact]
	public async Task It_reads_cached_input_tokens_off_the_usage_block()
	{
		// UsageDetails.CachedInputTokenCount is Microsoft.Extensions.AI's typed mapping of the
		// OpenAI-compatible wire field usage.prompt_tokens_details.cached_tokens. Meter() must
		// thread it into ModelUsage rather than silently drop it, same as it does for InputTokenCount.
		var usage = new Microsoft.Extensions.AI.UsageDetails
		{
			InputTokenCount = 900,
			OutputTokenCount = 40,
			CachedInputTokenCount = 600,
		};
		var reviewer = new ChatClientReviewer(
			[OpenRouter], env: _ => "key",
			clientFactory: (_, _, _, _) => new CapturingClient(_ => { }, usage));

		var reply = await reviewer.CompleteAsync(TaskFor().Request, "/tmp/demo");

		Assert.Equal(900, reply.Usage.InputTokens);
		Assert.Equal(40, reply.Usage.OutputTokens);
		Assert.Equal(600, reply.Usage.CachedInputTokens);
	}

	[Fact]
	public async Task Cached_input_tokens_default_to_zero_when_the_provider_reports_none()
	{
		var usage = new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 900, OutputTokenCount = 40 };
		var reviewer = new ChatClientReviewer(
			[OpenRouter], env: _ => "key",
			clientFactory: (_, _, _, _) => new CapturingClient(_ => { }, usage));

		var reply = await reviewer.CompleteAsync(TaskFor().Request, "/tmp/demo");

		Assert.Equal(0, reply.Usage.CachedInputTokens);
	}

	/// <summary>A fake IChatClient that records the options it was called with and returns a canned reply.</summary>
	private sealed class CapturingClient(
		System.Action<Microsoft.Extensions.AI.ChatOptions?> capture,
		Microsoft.Extensions.AI.UsageDetails? usage = null)
		: Microsoft.Extensions.AI.IChatClient
	{
		public void Dispose()
		{
		}

		public object? GetService(System.Type serviceType, object? serviceKey = null) => null;

		public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
			System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
			Microsoft.Extensions.AI.ChatOptions? options = null,
			System.Threading.CancellationToken cancellationToken = default)
		{
			capture(options);
			return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
				new Microsoft.Extensions.AI.ChatMessage(
					Microsoft.Extensions.AI.ChatRole.Assistant, """{"summary":"s","findings":[]}"""))
			{
				FinishReason = Microsoft.Extensions.AI.ChatFinishReason.Stop,
				Usage = usage,
			});
		}

		public System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
			System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
			Microsoft.Extensions.AI.ChatOptions? options = null,
			System.Threading.CancellationToken cancellationToken = default) =>
			throw new System.NotSupportedException();
	}
}
