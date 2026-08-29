using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>
/// Policy for trimming a diff before review: drop low-signal files matching
/// <see cref="IgnoreGlobs"/> (plus binary + rename-only files, handled intrinsically),
/// then keep the rest within <see cref="MaxBytes"/>. Lives in config; defaults below.
/// </summary>
public sealed record DiffFilterPolicy(IReadOnlyList<string>? IgnoreGlobs, int? MaxBytes)
{
	/// <summary>The byte budget a config inherits when it does not name one.</summary>
	public const int DefaultMaxBytes = 128 * 1024;

	/// <summary>
	/// The byte budget this filter actually enforces: the configured one if a config authored it -
	/// including a deliberate <c>0</c> - else <see cref="DefaultMaxBytes"/>.
	///
	/// <para><b>The single resolution point.</b> Every consumer calls this rather than reading
	/// <see cref="MaxBytes"/>, exactly as they call <see cref="Persona.SamplingTemperature"/>
	/// rather than reading <see cref="Persona.Temperature"/>. <see cref="MaxBytes"/> has to be
	/// nullable for the same reason that one is: a non-nullable <c>int</c> cannot tell an omitted
	/// JSON key from a deliberate <c>0</c>, so <c>{"filter": {"ignoreGlobs": ["*.log"]}}</c>
	/// decoded to a budget of zero bytes, <see cref="DiffFilter.Apply"/> omitted every file, and
	/// the panel reviewed an empty diff - which renders as a clean review rather than as the
	/// config error it is.</para>
	/// </summary>
	public int ByteBudget() => MaxBytes ?? DefaultMaxBytes;

	/// <summary>
	/// The globs to drop; empty when the config's <c>filter</c> block named none. Never null: a
	/// partial block such as <c>{"maxBytes": 200000}</c> reaches this constructor with a null
	/// list from any reflection-based codec, and the filter fold would then take down the review
	/// rather than report a config problem (#194). Note that an omitted <c>ignoreGlobs</c> means
	/// exactly what it says - ignore nothing - it does not inherit <see cref="Default"/>'s list.
	/// </summary>
	public IReadOnlyList<string> IgnoreGlobs { get; init; } = IgnoreGlobs ?? [];

	public static DiffFilterPolicy Default { get; } = new(
		[
			"*.lock", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "Cargo.lock", "packages.lock.json",
			"**/obj/**", "**/bin/**", "**/node_modules/**", "**/dist/**", "**/vendor/**",
			"*.min.js", "*.min.css", "*.Designer.cs", "*.g.cs", "*.generated.cs",
		],
		DefaultMaxBytes);
}

/// <summary>A file left out of the reviewed diff, with why.</summary>
public sealed record OmittedFile(string Path, string Reason);

/// <summary>The trimmed diff plus the files it left out (so the prompt can disclose them).</summary>
public sealed record FilteredDiff(Diff Diff, IReadOnlyList<OmittedFile> Omitted);

/// <summary>
/// Pure relevance filter + size cap. Drops binary, rename-only, and ignore-glob files,
/// then enforces the byte budget by omitting the largest remaining files first (so the
/// most files possible are reviewed). Rebuilds <see cref="Diff.Raw"/> from the kept
/// segments in original order. Never throws; an all-omitted result is valid (the caller
/// discloses it).
/// </summary>
public static class DiffFilter
{
	public static FilteredDiff Apply(Diff diff, DiffFilterPolicy policy)
	{
		var omitted = new List<OmittedFile>();
		var candidates = new List<DiffFile>();

		foreach (var f in diff.Files)
		{
			if (f.IsBinary)
			{
				omitted.Add(new OmittedFile(f.Path, "binary"));
			}
			else if (f.IsRenameOnly)
			{
				omitted.Add(new OmittedFile(f.Path, "rename-only"));
			}
			else if (policy.IgnoreGlobs.Any(g => Glob.IsMatch(g, f.Path)))
			{
				omitted.Add(new OmittedFile(f.Path, "ignored"));
			}
			else
			{
				candidates.Add(f);
			}
		}

		List<DiffFile> kept;
		if (candidates.Sum(Bytes) <= policy.ByteBudget())
		{
			kept = candidates;
		}
		else
		{
			// Drop the largest first until under budget; keep the survivors in original order.
			var keep = new HashSet<DiffFile>();
			var budget = policy.ByteBudget();
			foreach (var f in candidates.OrderBy(Bytes))
			{
				if (budget - Bytes(f) >= 0)
				{
					budget -= Bytes(f);
					keep.Add(f);
				}
				else
				{
					omitted.Add(new OmittedFile(f.Path, "size budget"));
				}
			}

			kept = candidates.Where(keep.Contains).ToList();
		}

		var rebuilt = string.Join("\n", kept.Select(k => k.Segment));
		return new FilteredDiff(new Diff(rebuilt, kept), omitted);
	}

	private static int Bytes(DiffFile f) => Encoding.UTF8.GetByteCount(f.Segment);
}
