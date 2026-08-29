using System;
using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// Builds an <see cref="IChatClient"/> for a provider/model. OpenRouter and Fireworks
/// both speak the OpenAI <c>/chat/completions</c> shape, so this is one OpenAI client
/// re-pointed at the provider's base URL via <see cref="OpenAIClientOptions.Endpoint"/>.
/// Function invocation is layered on so <see cref="ReviewTier.Agent"/> personas can run
/// the read-only repo tools in a loop - the capability that removes the need for any
/// external agent harness.
/// </summary>
internal static class ProviderClientFactory
{
	public static IChatClient Create(ProviderConfig provider, string modelId, string apiKey, TimeSpan networkTimeout)
	{
		var openAi = new OpenAIClient(
			new ApiKeyCredential(apiKey),
			// NetworkTimeout is the SDK's *per-attempt* limit (default 100s). On a large
			// diff a model can legitimately need longer to respond, so raise it to the
			// review budget; the TimeBox in ChatClientReviewer is the outer hard ceiling.
			new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl), NetworkTimeout = networkTimeout });

		return openAi.GetChatClient(modelId)
			.AsIChatClient()
			.AsBuilder()
			.UseFunctionInvocation() // no-op unless ChatOptions.Tools are supplied (agent tier)
			.Build();
	}
}
