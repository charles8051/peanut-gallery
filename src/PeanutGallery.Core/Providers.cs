namespace PeanutGallery.Core;

/// <summary>
/// An OpenAI-compatible model provider. Both OpenRouter and Fireworks expose the
/// OpenAI <c>/chat/completions</c> shape, so a provider is fully described by a
/// base URL and the environment variable holding its API key. The key value
/// itself never lives in the core (or in config on disk) - only the variable
/// name does; the shell reads the secret from the environment.
/// </summary>
/// <param name="Name">Logical name referenced by <see cref="ModelRef.Provider"/>.</param>
/// <param name="BaseUrl">OpenAI-compatible endpoint base, e.g.
/// <c>https://openrouter.ai/api/v1</c> or
/// <c>https://api.fireworks.ai/inference/v1</c>.</param>
/// <param name="ApiKeyEnv">Environment variable the shell reads the API key from.</param>
public sealed record ProviderConfig(string Name, string BaseUrl, string ApiKeyEnv);

/// <summary>Well-known provider defaults, for config scaffolding and validation hints.</summary>
public static class KnownProviders
{
	public const string OpenRouter = "openrouter";
	public const string Fireworks = "fireworks";

	public static ProviderConfig OpenRouterDefault { get; } =
		new(OpenRouter, "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY");

	public static ProviderConfig FireworksDefault { get; } =
		new(Fireworks, "https://api.fireworks.ai/inference/v1", "FIREWORKS_API_KEY");
}
