using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace PeanutGallery.Core;

/// <summary>
/// Minimal gitignore-ish glob matching for path filters. A pattern containing
/// <c>/</c> matches the full path; a pattern without one matches the basename
/// (so <c>*.lock</c> matches any <c>.lock</c> file anywhere). <c>**</c> spans
/// directories, <c>*</c> spans within a segment, <c>?</c> matches one char.
/// Interpreted (not <c>RegexOptions.Compiled</c>) so the core stays AOT-clean;
/// compiled patterns are cached.
/// </summary>
public static class Glob
{
	private static readonly ConcurrentDictionary<string, Regex> Cache = new();

	public static bool IsMatch(string pattern, string path)
	{
		var normalized = path.Replace('\\', '/');
		var target = pattern.Contains('/') ? normalized : normalized[(normalized.LastIndexOf('/') + 1)..];
		return Cache.GetOrAdd(pattern, Compile).IsMatch(target);
	}

	private static Regex Compile(string glob)
	{
		var sb = new StringBuilder("^");
		for (var i = 0; i < glob.Length; i++)
		{
			var c = glob[i];
			switch (c)
			{
				case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
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
