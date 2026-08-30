using System.Reflection;

namespace PeanutGallery.Cli;

/// <summary>
/// The CLI shell: the first projection of the pure core onto a real interface. It
/// owns argument parsing, config IO, and (eventually) the model client - the core
/// owns the review logic. Arg parsing is hand-rolled (no reflection) so this shell
/// can be published Native-AOT later.
/// </summary>
internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		if (args.Length == 0)
		{
			Help.Print();
			return 0;
		}

		var verb = args[0];
		switch (verb)
		{
			case "-h" or "--help" or "help":
				Help.Print();
				return 0;
			case "-v" or "--version":
				Console.WriteLine(Version);
				return 0;
		}

		var a = Args.Parse(args[1..]);
		try
		{
			return verb switch
			{
				"init" => Commands.Init(a),
				"personas" => Commands.Personas(a),
				"validate" => Commands.Validate(a),
				"plan" => Commands.Plan(a),
				"review" => await Commands.ReviewAsync(a),
				"review-pr" => await Commands.ReviewPrAsync(a),
				"await-review" => await Commands.AwaitReviewAsync(a),
				"metrics" => await Commands.MetricsAsync(a),
				_ => Unknown(verb),
			};
		}
		catch (CliError e)
		{
			Console.Error.WriteLine("error: " + e.Message);
			return 1;
		}
	}

	private static int Unknown(string verb)
	{
		Console.Error.WriteLine($"error: unknown command '{verb}'");
		Help.Print();
		return 1;
	}

	public static string Version =>
		typeof(Program).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? "0.0.0-dev";
}

/// <summary>A user-facing error: printed without a stack trace, exits non-zero.</summary>
internal sealed class CliError(string message) : Exception(message);

/// <summary>Minimal, reflection-free argument bag: <c>--key value</c>, <c>--flag</c>, and positionals.</summary>
internal sealed class Args
{
	private readonly Dictionary<string, string> _opts;

	private Args(Dictionary<string, string> opts, IReadOnlyList<string> positionals)
	{
		_opts = opts;
		Positionals = positionals;
	}

	public IReadOnlyList<string> Positionals { get; }

	public string? Get(string key) => _opts.TryGetValue(key, out var v) ? v : null;

	public string GetOr(string key, string fallback) => Get(key) ?? fallback;

	public string Require(string key) => Get(key) ?? throw new CliError($"missing required --{key}");

	public bool Flag(string key) => _opts.TryGetValue(key, out var v) && v is not "false";

	public static Args Parse(string[] tokens)
	{
		var opts = new Dictionary<string, string>(StringComparer.Ordinal);
		var positionals = new List<string>();

		for (var i = 0; i < tokens.Length; i++)
		{
			var token = tokens[i];
			if (token.StartsWith("--", StringComparison.Ordinal))
			{
				var key = token[2..];
				var hasValue = i + 1 < tokens.Length && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal);
				opts[key] = hasValue ? tokens[++i] : "true";
			}
			else
			{
				positionals.Add(token);
			}
		}

		return new Args(opts, positionals);
	}
}
