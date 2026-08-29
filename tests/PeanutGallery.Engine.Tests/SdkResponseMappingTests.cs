using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// A contract check on the OpenAI SDK's response mapping, driven by canned wire JSON through the
/// real client pipeline (no network). It exists to hold down the shape behind #158: which provider
/// replies the SDK refuses to map, and with what exception — the fact
/// <see cref="TransientFailure.IsMalformedResponse"/> is keyed on. If an SDK upgrade changes that
/// shape, this fails here rather than silently in production a week later.
/// </summary>
public class SdkResponseMappingTests
{
	private sealed class Canned(string body) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			});
	}

	// The production pipeline from ProviderClientFactory, pointed at a canned response.
	private static IChatClient Client(string body) =>
		new OpenAIClient(
				new ApiKeyCredential("k"),
				new OpenAIClientOptions
				{
					Endpoint = new Uri("https://example.invalid/v1"),
					Transport = new HttpClientPipelineTransport(new HttpClient(new Canned(body))),
				})
			.GetChatClient("m")
			.AsIChatClient()
			.AsBuilder()
			.UseFunctionInvocation()
			.Build();

	private static Task<ChatResponse> Ask(string body) =>
		Client(body).GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

	[Theory]
	// OpenRouter's shape when the upstream generation failed: an error envelope and NO choices. This
	// is the sibling of #113 - the same upstream failure, reported on the wire instead of as
	// finish_reason:"error" - and it is what cost one repo four consecutive panel-wide turns.
	[InlineData("""{"error":{"message":"upstream failed","code":502},"id":"x","object":"chat.completion","created":1,"model":"m","choices":[]}""")]
	// The same thing without the envelope.
	[InlineData("""{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[]}""")]
	public async Task A_completion_with_no_choices_is_unmappable_and_reads_as_retryable(string body)
	{
		var e = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Ask(body));

		// The signal the boundary keys on - the type and the parameter name, never the message text,
		// which varies with the SDK build (production's wording differs from this one's).
		Assert.Equal("index", e.ParamName);
		Assert.True(TransientFailure.IsSdkMappingFailure(e));
	}

	[Fact]
	public async Task The_call_boundary_turns_that_into_a_retryable_MalformedResponse()
	{
		// End to end through the real reviewer: the shape is recognised where the SDK's mapping is
		// the only code in scope, and leaves as a TYPE, so nothing downstream has to guess.
		//
		// maxAttempts:1 so this test is about the boundary, not the retry loop, and the type arrives
		// UNWRAPPED - RetryingModelCall.Enrich preserves the original exception at attempts <= 1 and
		// only annotates once a retry actually happened. The exhausted-retry case (wrapped in a
		// ModelCallException) is covered in RetryingModelCallTests, where the backoff is injected
		// and costs no wall-clock.
		var reviewer = new ChatClientReviewer(
			[new PeanutGallery.Core.ProviderConfig("openrouter", "https://example.invalid/v1", "PG_TEST_KEY")],
			_ => "k",
			maxAttempts: 1,
			clientFactory: (_, _, _, _) => Client(
				"""{"error":{"message":"upstream failed"},"id":"x","object":"chat.completion","created":1,"model":"m","choices":[]}"""));

		var e = await Assert.ThrowsAsync<MalformedResponseException>(() => reviewer.CompleteAsync(
			new PeanutGallery.Core.ReviewRequest(
				new PeanutGallery.Core.ModelRef("openrouter", "m"),
				0.2,
				PeanutGallery.Core.ReviewTier.Diff,
				[new PeanutGallery.Core.Message(PeanutGallery.Core.ChatRole.User, "review this")]),
			repoPath: "."));

		Assert.True(TransientFailure.IsRetryable(e));
		Assert.True(TransientFailure.IsMalformedResponse(e));
	}

	[Theory]
	// A reply that is genuinely EMPTY maps fine and must keep reaching the shrink-retry ladder as an
	// empty reply (#109) - it is a different failure from an unmappable one, and conflating them
	// would send it down the wrong recovery path.
	[InlineData("""{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":null},"finish_reason":"stop"}]}""")]
	[InlineData("""{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"stop"}]}""")]
	// A reasoning-only reply (the model spent its budget thinking) likewise maps to empty text.
	[InlineData("""{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","reasoning":"thinking"},"finish_reason":"stop"}]}""")]
	public async Task An_empty_reply_still_maps_and_is_not_treated_as_malformed(string body)
	{
		var response = await Ask(body);
		Assert.Equal(string.Empty, response.Text);
	}
}
