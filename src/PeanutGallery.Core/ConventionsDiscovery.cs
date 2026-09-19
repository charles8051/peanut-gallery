using System;
using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>
/// Where to look for a repo's conventions, given the files a change touches.
///
/// <para>The repo root is not the only place a team writes its rules down: a monorepo puts the
/// rules for its tests in <c>tests/CLAUDE.md</c> and the rules for a service beside that service.
/// Reading only the root meant a repo whose sole conventions file sat one directory down was
/// reviewed with no house rules at all, which reads exactly like a repo that has none.</para>
///
/// <para>Pure by design (ADR-0001): this turns changed-file paths into an ordered list of paths
/// to probe and never touches a filesystem or the network. The shells - the CLI reading a
/// checkout, the desktop app fetching from GitHub at the PR head - own the IO and keep the first
/// hit in each chain.</para>
/// </summary>
public static class ConventionsDiscovery
{
	/// <summary>
	/// Repo-root candidates, most specific first. The Copilot file wins when present because it is
	/// unambiguously written FOR a code reviewer; the agent files are broader (build commands,
	/// workflow) but still carry the design rules that matter most.
	/// </summary>
	public static readonly IReadOnlyList<string> RootCandidates =
	[
		".github/copilot-instructions.md",
		".github/peanut-gallery-instructions.md",
		"CLAUDE.md",
		"AGENTS.md",
	];

	/// <summary>
	/// What counts as conventions below the root. The two <c>.github/</c> files are repo-wide by
	/// convention and are not looked for in a subdirectory.
	/// </summary>
	public static readonly IReadOnlyList<string> ScopedCandidates = ["CLAUDE.md", "AGENTS.md"];

	/// <summary>
	/// Cap on how many directories are searched. A wide change can touch dozens, and every one is
	/// probe IO on the desktop path (a GitHub call per candidate). Eight subtrees is far more than
	/// any real repo nests conventions in, and the prompt budget could not carry more anyway.
	/// </summary>
	public const int DefaultMaxScopes = 8;

	/// <summary>Bound on the walk up a single path, finite against an absurdly deep diff entry.</summary>
	private const int MaxDepth = 32;

	/// <summary>
	/// One chain per directory the changed files live in, each ordered nearest-first: every
	/// candidate filename in the file's own directory, then its parent's, up to but excluding the
	/// repo root. A shell probes a chain in order and keeps the FIRST hit - that is the
	/// nearest-ancestor rule - then moves to the next chain.
	///
	/// <para>The root itself is deliberately absent: <see cref="RootCandidates"/> covers it, it is
	/// a different (wider) candidate list, and it applies whether or not a changed file sits in a
	/// subdirectory.</para>
	///
	/// <para>Diff paths are attacker-controlled on any PR, so anything that could climb out of the
	/// repo - an absolute path, a <c>..</c> segment - is dropped here rather than left for a shell
	/// to notice. Chains are ordinal-sorted by directory so the same change always probes in the
	/// same order, and truncation by <paramref name="maxScopes"/> is therefore deterministic too.</para>
	/// </summary>
	public static IReadOnlyList<IReadOnlyList<string>> ScopeChains(
		IEnumerable<string>? changedFiles, int maxScopes = DefaultMaxScopes)
	{
		if (changedFiles is null || maxScopes <= 0)
		{
			return [];
		}

		var directories = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var file in changedFiles)
		{
			if (Directory(file) is { Length: > 0 } dir)
			{
				directories.Add(dir);
			}
		}

		var chains = new List<IReadOnlyList<string>>();
		foreach (var dir in directories)
		{
			if (chains.Count == maxScopes)
			{
				break;
			}

			chains.Add(Chain(dir));
		}

		return chains;
	}

	/// <summary>
	/// The directory a conventions file governs: its own, or empty for a repo-wide file at the
	/// root. Shells stamp this onto a scoped hit so the prompt can say which subtree it rules.
	/// </summary>
	public static string ScopeOf(string? path)
	{
		var dir = Directory(path);
		return dir ?? string.Empty;
	}

	/// <summary>
	/// The directory part of a diff path, normalised to forward slashes and relative to the repo
	/// root, or null when the path is root-level, malformed, or escapes the repo.
	/// </summary>
	private static string? Directory(string? file)
	{
		if (string.IsNullOrWhiteSpace(file))
		{
			return null;
		}

		var path = file.Replace('\\', '/').Trim();
		while (path.StartsWith("./", StringComparison.Ordinal))
		{
			path = path[2..];
		}

		// Rooted or drive-qualified: not a repo-relative diff path at all.
		if (path.StartsWith('/') || (path.Length > 1 && path[1] == ':'))
		{
			return null;
		}

		var cut = path.LastIndexOf('/');
		if (cut <= 0)
		{
			return null; // root-level file: RootCandidates already covers it
		}

		var dir = path[..cut];
		foreach (var segment in dir.Split('/'))
		{
			if (segment is ".." or "" or ".")
			{
				return null; // an escape, or a path we cannot normalise confidently
			}
		}

		return dir;
	}

	/// <summary>Every candidate path from <paramref name="directory"/> up to, but not including, the root.</summary>
	private static IReadOnlyList<string> Chain(string directory)
	{
		var paths = new List<string>();
		var dir = directory;
		for (var depth = 0; dir.Length > 0 && depth < MaxDepth; depth++)
		{
			foreach (var name in ScopedCandidates)
			{
				paths.Add(dir + "/" + name);
			}

			var cut = dir.LastIndexOf('/');
			dir = cut < 0 ? string.Empty : dir[..cut];
		}

		return paths;
	}
}
