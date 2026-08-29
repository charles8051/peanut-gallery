using System.IO;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class RepoToolsTests
{
	[Fact]
	public void Reads_a_file_inside_the_sandbox_and_refuses_escapes()
	{
		var dir = Directory.CreateTempSubdirectory("pg-tools-").FullName;
		try
		{
			File.WriteAllText(Path.Combine(dir, "hello.txt"), "hi there");
			var tools = new RepoTools(dir);

			Assert.Equal("hi there", tools.ReadFile("hello.txt"));
			Assert.StartsWith("error: file not found", tools.ReadFile("missing.txt"));

			// Traversal outside the root is refused, returned as an error string.
			Assert.StartsWith("error: path", tools.ReadFile("../escape.txt"));
			Assert.Null(tools.Resolve("../../etc/passwd"));
			Assert.NotNull(tools.Resolve("hello.txt"));
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void Glob_matches_files_and_grep_finds_content()
	{
		var dir = Directory.CreateTempSubdirectory("pg-tools-").FullName;
		try
		{
			Directory.CreateDirectory(Path.Combine(dir, "src"));
			File.WriteAllText(Path.Combine(dir, "src", "Foo.cs"), "class Foo { }\nvar bug = 1;\n");
			File.WriteAllText(Path.Combine(dir, "README.md"), "# readme\n");
			var tools = new RepoTools(dir);

			Assert.Contains("src/Foo.cs", tools.Glob("src/**/*.cs"));
			Assert.DoesNotContain("README.md", tools.Glob("src/**/*.cs"));

			var grep = tools.Grep("bug");
			Assert.Contains("src/Foo.cs:2:", grep);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}
}
