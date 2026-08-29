using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>Severity of a single finding, ascending. Used to sort and to gate.</summary>
public enum Severity
{
	Info,
	Minor,
	Major,
	Critical,
}

/// <summary>
/// One reviewer finding. <see cref="Line"/> is 0 when the finding is not tied to
/// a specific line (e.g. a contrarian's "delete this whole subsystem").
/// <para><see cref="Confidence"/> is the reviewer's own certainty that the finding is
/// real and correct, 0.0-1.0. It defaults to 1.0 so a finding from a model (or a stored
/// session) that never supplied one is treated as fully confident - i.e. the gate can
/// only ever suppress findings that explicitly admit doubt, never silently hide legacy
/// ones. See <see cref="ConfidenceGate"/>.</para>
/// </summary>
public sealed record Finding(
	Severity Severity,
	string File,
	int Line,
	string Title,
	string Body,
	double Confidence = 1.0);

/// <summary>
/// The outcome of one persona's review of one task: its findings plus, optionally,
/// the raw model text (kept for debugging / re-rendering). Produced by the shell,
/// rendered to a PR comment by the pure <see cref="CommentRenderer"/>.
/// </summary>
public sealed record PersonaReview(
	Persona Persona,
	RepoTarget Repo,
	IReadOnlyList<Finding> Findings,
	string? RawModelText = null);
