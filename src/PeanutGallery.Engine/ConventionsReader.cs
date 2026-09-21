using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// Collects the conventions that govern a change, given a way to read one candidate path.
///
/// <para>The walk is <see cref="ConventionsDiscovery"/>'s and pure; the IO is the caller's - a
/// checkout on the CLI, a GitHub fetch at the PR head on the desktop. This fold sits between the
/// two so they cannot drift on which files win: the repo-wide file at the root always applies,
/// and within a subtree the NEAREST file supersedes the ones above it.</para>
/// </summary>
public static class ConventionsReader
{
	/// <summary>No ceiling on probes - the right choice when a probe is a local file stat.</summary>
	public const int Unbounded = 0;

	/// <summary>
	/// Root conventions plus the nearest file above each directory <paramref name="changedFiles"/>
	/// touches, or null when none apply. <paramref name="probe"/> returns a candidate's text, or
	/// null when it is missing, empty, or not worth reading; it is called at most once per path,
	/// which matters because chains overlap heavily (every file under <c>src/</c> shares
	/// <c>src/</c>).
	///
	/// <para><paramref name="maxProbes"/> bounds the TOTAL paths read, root pass included, for a
	/// caller whose probe costs a network round trip. Hitting it ends the subtree pass and keeps
	/// whatever was found, because partial house rules beat none and beat a stalled review.</para>
	/// </summary>
	public static async Task<RepoConventions?> CollectAsync(
		Func<string, CancellationToken, Task<string?>> probe,
		IEnumerable<string>? changedFiles,
		int maxProbes = Unbounded,
		CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(probe);

		var probed = new Dictionary<string, string?>(StringComparer.Ordinal);

		async Task<string?> ProbeOnceAsync(string candidate)
		{
			if (probed.TryGetValue(candidate, out var cached))
			{
				return cached;
			}

			var text = await probe(candidate, ct);
			text = string.IsNullOrWhiteSpace(text) ? null : text;
			probed[candidate] = text;
			return text;
		}

		var found = new List<ConventionsFile>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var candidate in ConventionsDiscovery.RootCandidates)
		{
			ct.ThrowIfCancellationRequested();
			if (await ProbeOnceAsync(candidate) is { } text)
			{
				found.Add(new ConventionsFile(candidate, text));
				seen.Add(candidate);
				break; // most specific root candidate wins; the rest say the same thing worse
			}
		}

		foreach (var chain in ConventionsDiscovery.ScopeChains(changedFiles))
		{
			foreach (var candidate in chain)
			{
				ct.ThrowIfCancellationRequested();
				if (maxProbes > Unbounded && probed.Count >= maxProbes && !probed.ContainsKey(candidate))
				{
					return Result(found);
				}

				if (await ProbeOnceAsync(candidate) is not { } text)
				{
					continue;
				}

				// Deduped because two directories can share the same nearest ancestor; the break
				// is what makes it NEAREST-ancestor rather than every-ancestor.
				if (seen.Add(candidate))
				{
					found.Add(new ConventionsFile(candidate, text, ConventionsDiscovery.ScopeOf(candidate)));
				}

				break;
			}
		}

		return Result(found);

		static RepoConventions? Result(List<ConventionsFile> files) =>
			files.Count == 0 ? null : new RepoConventions(files);
	}
}
