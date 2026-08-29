using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace PeanutGallery.Engine;

/// <summary>
/// The read-only tool set handed to <see cref="Core.ReviewTier.Agent"/> personas:
/// read a file, grep, glob - all sandboxed to one repository root, with output caps.
/// There is no write, no shell-out, and no network here, which is what makes letting a
/// model drive these tools safe and why owning them is better than handing a third-party
/// harness shell access. Path traversal outside the root is refused (returned to the
/// model as an error string, never thrown).
/// </summary>
public sealed class RepoTools
{
	private const int MaxFileBytes = 64 * 1024;
	private const int MaxMatches = 200;

	private static readonly string[] SkipDirs =
		["bin", "obj", ".git", ".vs", "node_modules", "artifacts", ".roam", ".repowise"];

	private readonly string _root;

	public RepoTools(string repoPath) =>
		_root = Path.GetFullPath(Directory.Exists(repoPath) ? repoPath : ".");

	/// <summary>The tools as Microsoft.Extensions.AI <see cref="AITool"/>s for <c>ChatOptions.Tools</c>.</summary>
	public IList<AITool> AsTools() =>
	[
		AIFunctionFactory.Create(ReadFile),
		AIFunctionFactory.Create(Grep),
		AIFunctionFactory.Create(Glob),
	];

	[Description("Read a UTF-8 text file from the repository under review. The path is relative to the repo root.")]
	public string ReadFile([Description("Repo-relative file path")] string path)
	{
		var full = Resolve(path);
		if (full is null)
		{
			return $"error: path '{path}' is outside the repository";
		}

		if (!File.Exists(full))
		{
			return $"error: file not found: {path}";
		}

		var bytes = File.ReadAllBytes(full);
		var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxFileBytes));
		return bytes.Length > MaxFileBytes ? text + "\n... [truncated]" : text;
	}

	[Description("Search repository file contents for a .NET regular expression. Returns up to 200 'path:line: text' matches.")]
	public string Grep([Description("A .NET regular expression")] string pattern)
	{
		Regex regex;
		try
		{
			regex = CompileSearchPattern(pattern);
		}
		catch (ArgumentException e)
		{
			return $"error: invalid regex: {e.Message}";
		}

		var sb = new StringBuilder();
		var count = 0;
		foreach (var file in EnumerateFiles())
		{
			string[] lines;
			try
			{
				lines = File.ReadAllLines(file);
			}
			catch (IOException)
			{
				continue;
			}

			for (var i = 0; i < lines.Length; i++)
			{
				bool matched;
				try
				{
					matched = regex.IsMatch(lines[i]);
				}
				catch (RegexMatchTimeoutException)
				{
					// Abort the whole scan, not just this line. A pattern that blows the budget
					// once will blow it on the next long line too, so continuing would multiply
					// the timeout by the file count and reintroduce exactly the hang being fixed.
					return "error: the regex took too long to evaluate and was abandoned "
						+ "(it may backtrack catastrophically); try a simpler pattern.";
				}

				if (!matched)
				{
					continue;
				}

				sb.Append(Rel(file)).Append(':').Append(i + 1).Append(": ").Append(lines[i].Trim()).Append('\n');
				if (++count >= MaxMatches)
				{
					return sb.Append("... [truncated]\n").ToString();
				}
			}
		}

		return count == 0 ? "(no matches)" : sb.ToString();
	}

	[Description("List repository files matching a glob, e.g. 'src/**/*.cs'. Returns up to 200 repo-relative paths.")]
	public string Glob([Description("A glob pattern")] string pattern)
	{
		var regex = GlobToRegex(pattern);
		var matches = EnumerateFiles().Select(Rel).Where(r => regex.IsMatch(r)).Take(MaxMatches).ToList();
		return matches.Count == 0 ? "(no matches)" : string.Join('\n', matches);
	}

	/// <summary>
	/// Options that keep the walk itself inside the checkout.
	///
	/// <para><see cref="SearchOption.AllDirectories"/> recurses THROUGH a directory symlink, so a
	/// link committed in a PR - <c>vendor -&gt; /</c> - is walked before anything gets a chance to
	/// filter it. Discarding those files afterwards returns nothing outside the root but still pays
	/// for the traversal, which is the whole cost: a link to a large external tree exhausts the
	/// review's time budget statting files it was always going to throw away. Skipping reparse
	/// points stops the recursion instead of cleaning up after it, and drops symlinked leaves on
	/// the same pass.</para>
	///
	/// <para>The two properties left alone are left alone deliberately, and the concrete values
	/// are worth writing down because the <see cref="SearchOption"/> overload does NOT use the
	/// property defaults. It builds compatibility options of
	/// <c>AttributesToSkip = None, IgnoreInaccessible = false</c>, whereas
	/// <c>new EnumerationOptions()</c> starts at <c>Hidden | System</c> and <c>true</c>. So
	/// assigning <see cref="FileAttributes.ReparsePoint"/> outright keeps hidden and system files
	/// enumerated exactly as before rather than newly skipping them, and
	/// <see cref="EnumerationOptions.IgnoreInaccessible"/> arrives already true - a directory this
	/// process cannot read is not worth sinking a whole grep over - though it is assigned below
	/// regardless, because the value it arrives at is not the one people expect.</para>
	///
	/// <para>Skipping reparse points is a policy for the WALK, not a containment check that leaks:
	/// a symlink whose target is inside the root is not enumerated either. That is deliberate and
	/// matches every search tool in the ecosystem - ripgrep needs <c>--follow</c>, and git grep
	/// searches a link's blob (the target path as text) rather than the target's content. Following
	/// them here would report one line of code at two paths, and the link is not the path a
	/// reviewer should be sent to. No content goes unsearched either way: the target of an
	/// inside-the-root link is itself inside the root, so the walk reaches it on its own and the
	/// only thing dropped is the duplicate path. <c>read_file</c> is the other case and behaves
	/// the other way:
	/// the model named that exact path, so <see cref="Resolve"/> resolves the link and allows it
	/// when the target is inside the root.</para>
	/// </summary>
	private static readonly EnumerationOptions ContainedWalk = new()
	{
		RecurseSubdirectories = true,
		AttributesToSkip = FileAttributes.ReparsePoint,

		// Already the property default. Written out anyway: this one is misremembered as false
		// often enough that a reader checking whether a grep can be aborted by one unreadable
		// directory should not have to go and look it up.
		IgnoreInaccessible = true,
	};

	/// <summary>
	/// Every file the tools may look at. Containment here is a property of the walk, not a filter
	/// over its results - with no reparse point anywhere on the path, a path under the root IS
	/// under the root. <see cref="Resolve"/> is where a resolved-path check is still needed,
	/// because <c>read_file</c> takes a path from the model rather than from this enumeration.
	/// </summary>
	private IEnumerable<string> EnumerateFiles() =>
		Directory.EnumerateFiles(_root, "*", ContainedWalk).Where(f => !IsSkipped(f));

	private bool IsSkipped(string file) =>
		Rel(file).Split('/').Any(seg => SkipDirs.Contains(seg, StringComparer.OrdinalIgnoreCase));

	private string Rel(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');

	/// <summary>
	/// Resolve a repo-relative path inside the root, or null if it escapes the sandbox.
	///
	/// <para>Containment is decided by <see cref="FileSystemSafety.ResolvesInsideRoot"/>, not by
	/// comparing normalised strings. <see cref="Path.GetFullPath(string)"/> collapses <c>..</c>
	/// without following reparse points, so a symlink committed in the repo and pointing outside
	/// it looks contained at every step and still reads the target - and the path handed to these
	/// tools comes from a model reading an attacker-controlled diff.</para>
	/// </summary>
	public string? Resolve(string path)
	{
		var full = Path.GetFullPath(Path.Combine(_root, path));
		return FileSystemSafety.ResolvesInsideRoot(_root, full) ? full : null;
	}

	/// <summary>
	/// Budget for a single line match on the backtracking fallback. Small on purpose: no honest
	/// search of one line needs longer, and this is the ceiling on how much a hostile pattern can
	/// cost before it is abandoned.
	/// </summary>
	private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

	/// <summary>
	/// Compile a MODEL-SUPPLIED pattern safely.
	///
	/// <para>The pattern reaches here from an agent-tier reviewer, which reads a diff - untrusted
	/// on any PR - so a prompt-injected change could try to hand the tool a catastrophically
	/// backtracking regex and hang the run until the outer per-call timeout fires.</para>
	///
	/// <para>Preference order matters. <see cref="RegexOptions.NonBacktracking"/> makes the hang
	/// IMPOSSIBLE rather than merely bounded - it guarantees linear time - so it is tried first
	/// and covers the overwhelming majority of search patterns. It cannot express backreferences,
	/// lookarounds, or atomic groups, and rejecting those outright would quietly break legitimate
	/// searches, so those fall back to the backtracking engine WITH a match timeout: a bound
	/// rather than a guarantee, which is the best available for that class.</para>
	/// </summary>
	private static Regex CompileSearchPattern(string pattern)
	{
		try
		{
			// NonBacktracking and Compiled are mutually exclusive; the linear-time guarantee is
			// worth more here than the compilation speed-up.
			return new Regex(pattern, RegexOptions.NonBacktracking, MatchTimeout);
		}
		catch (NotSupportedException)
		{
			// A construct NonBacktracking cannot express. Fall back, bounded.
			return new Regex(pattern, RegexOptions.Compiled, MatchTimeout);
		}
	}

	private static Regex GlobToRegex(string glob)
	{
		var sb = new StringBuilder("^");
		for (var i = 0; i < glob.Length; i++)
		{
			var c = glob[i];
			switch (c)
			{
				case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
					// Globstar. "**/" matches zero or more directory segments (so
					// 'src/**/*.cs' matches 'src/Foo.cs' as well as 'src/a/b/Foo.cs').
					if (i + 2 < glob.Length && glob[i + 2] == '/')
					{
						sb.Append("(?:.*/)?");
						i += 2;
					}
					else
					{
						sb.Append(".*");
						i++;
					}

					break;
				case '*':
					sb.Append("[^/]*");
					break;
				case '?':
					sb.Append('.');
					break;
				case '.' or '(' or ')' or '+' or '|' or '^' or '$' or '\\' or '{' or '}' or '[' or ']':
					sb.Append('\\').Append(c);
					break;
				default:
					sb.Append(c);
					break;
			}
		}

		sb.Append('$');
		return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
	}
}
