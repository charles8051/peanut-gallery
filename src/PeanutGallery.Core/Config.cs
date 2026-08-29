using System.Collections.Generic;
using System.Linq;

namespace PeanutGallery.Core;

/// <summary>
/// The whole Peanut Gallery configuration as an immutable value: the providers
/// reviews can use, the personas that can review, the repos under management, and
/// the persona to repo assignments. This is the single source of truth a shell
/// loads, edits (the GUI/server), and feeds to the <see cref="ReviewPlanner"/>.
/// The core defines it as plain data with no serialization concern; a shell owns
/// reading and writing it (JSON today, anything later).
///
/// <para><b>The four collections are never null, whatever a decoder passes.</b> Every shell
/// deserializes this record through a reflection-based codec, and a codec hands a constructor
/// <c>null</c> for any key the JSON omits — so a config that simply left <c>personas</c> out
/// (the natural shape under <see cref="PanelMode.Auto"/>, where there are no personas to
/// declare) used to reach the first consumer as a null list and take down <c>validate</c>, the
/// one command whose job is to explain what is wrong with a config, with an unhandled
/// <c>NullReferenceException</c> (#194). The normalization lives here rather than in a codec
/// because there is more than one decode path — the CLI's file read and the desktop's
/// fetched-bytes read — and a second codec must not be able to reintroduce it, which is the
/// same two-decoders-disagree shape as #127.</para>
/// </summary>
public sealed record PeanutConfig(
	IReadOnlyList<ProviderConfig>? Providers,
	IReadOnlyList<Persona>? Personas,
	IReadOnlyList<RepoTarget>? Repos,
	IReadOnlyList<Assignment>? Assignments,
	DiffFilterPolicy? Filter = null,
	SkipPolicy? Skip = null,
	double? MinConfidence = null,
	bool? Verify = null,
	CommentMode? Comment = null,
	PanelMode? Panel = null,
	ModelRef? Orchestrator = null,
	ModelRef? PersonaModel = null,
	double? PersonaTemperature = null,
	double? PersonaTopP = null,
	int? PersonaTopK = null,
	ConversationPolicy? Conversation = null)
{
	/// <summary>The providers reviews can reach; empty when the config declares none.</summary>
	public IReadOnlyList<ProviderConfig> Providers { get; init; } = Providers ?? [];

	/// <summary>The configured personas; empty is valid under <see cref="PanelMode.Auto"/>.</summary>
	public IReadOnlyList<Persona> Personas { get; init; } = Personas ?? [];

	/// <summary>The repos under management; empty when the config declares none.</summary>
	public IReadOnlyList<RepoTarget> Repos { get; init; } = Repos ?? [];

	/// <summary>Persona to repo assignments; empty is valid under <see cref="PanelMode.Auto"/>.</summary>
	public IReadOnlyList<Assignment> Assignments { get; init; } = Assignments ?? [];

	public static PeanutConfig Empty { get; } = new([], [], [], []);

	public Persona? FindPersona(string id) =>
		Personas.FirstOrDefault(p => p.Id == id);

	public RepoTarget? FindRepo(string name) =>
		Repos.FirstOrDefault(r => r.Name == name);

	public ProviderConfig? FindProvider(string name) =>
		Providers.FirstOrDefault(p => p.Name == name);
}
