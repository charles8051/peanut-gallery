using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// The port a shell implements to actually run a review task against a model. This
/// is the async, IO-bearing boundary - deliberately a shell concern, never in the
/// pure core (ADR-0001). Implementations must be total at this seam: a provider
/// outage or a missing key becomes a failure <see cref="Finding"/>, not a throw, so
/// one persona's trouble never sinks the whole fan-out.
/// </summary>
public interface IReviewer
{
	/// <summary>Stateless review of one task; total (failures become a finding).</summary>
	Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default);

	/// <summary>
	/// The raw model call for a pre-assembled request; throws on failure. Used by the stateful
	/// PR-session shell. Returns the reply AND what it cost - see <see cref="ModelReply"/> for why
	/// the two travel together rather than usage being reported out of band.
	/// </summary>
	Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default);
}
