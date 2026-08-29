using PeanutGallery.Core;
using PeanutGallery.Engine;

namespace PeanutGallery.Cli;

/// <summary>
/// The CLI's file-IO wrapper over the shared <see cref="ConfigCodec"/> — the JSON shape lives
/// there so the CLI and the desktop GUI read the identical format. This layer owns only the
/// file read/write, CliError wrapping, and the CLI's generic JSON pretty-printing for output.
/// </summary>
internal static class ConfigIo
{
	public static PeanutConfig Load(string path)
	{
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (IOException e)
		{
			throw new CliError($"could not read config '{path}': {e.Message}");
		}

		try
		{
			return ConfigCodec.Parse(text);
		}
		catch (ConfigFormatException e)
		{
			throw new CliError($"invalid config JSON in '{path}': {e.Message}");
		}
	}

	public static void Save(string path, PeanutConfig config) =>
		File.WriteAllText(path, ConfigCodec.Serialize(config) + "\n");

	/// <summary>Pretty-print any value as JSON for CLI output, using the shared config options.</summary>
	public static string Serialize<T>(T value) => ConfigCodec.Serialize(value);
}
