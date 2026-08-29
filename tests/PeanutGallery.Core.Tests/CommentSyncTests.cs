using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class CommentSyncTests
{
	private static string Rendered(string personaId, string note) =>
		$"{CommentRenderer.Marker(personaId)}\n### {personaId}\n\n{note}\n";

	[Fact]
	public void New_persona_with_no_existing_comment_is_a_create()
	{
		var plan = CommentSync.Plan([], [Rendered("architect", "hi")]);

		var op = Assert.Single(plan);
		Assert.Equal(UpsertAction.Create, op.Action);
		Assert.Null(op.CommentId);
	}

	[Fact]
	public void Existing_comment_with_the_same_marker_is_updated_in_place()
	{
		var existing = new[]
		{
			new ExistingComment(101, "some unrelated human comment"),
			new ExistingComment(202, CommentRenderer.Marker("architect") + "\n### old body"),
		};

		var op = Assert.Single(CommentSync.Plan(existing, [Rendered("architect", "new body")]));

		Assert.Equal(UpsertAction.Update, op.Action);
		Assert.Equal(202, op.CommentId);
		Assert.Contains("new body", op.Body);
	}

	[Fact]
	public void Mixed_personas_create_the_new_and_update_the_known()
	{
		var existing = new[] { new ExistingComment(7, CommentRenderer.Marker("bug-hunter") + "\nprev") };

		var plan = CommentSync.Plan(existing, [Rendered("architect", "a"), Rendered("bug-hunter", "b")]);

		Assert.Equal(UpsertAction.Create, plan[0].Action);          // architect: brand new
		Assert.Equal(UpsertAction.Update, plan[1].Action);          // bug-hunter: in place
		Assert.Equal(7, plan[1].CommentId);
	}

	[Fact]
	public void Markers_are_persona_specific_and_do_not_cross_match()
	{
		var existing = new[] { new ExistingComment(5, CommentRenderer.Marker("architect") + "\nx") };

		// The contrarian must not hijack the architect's comment.
		var op = Assert.Single(CommentSync.Plan(existing, [Rendered("contrarian", "y")]));
		Assert.Equal(UpsertAction.Create, op.Action);
	}

	[Fact]
	public void A_body_without_a_marker_is_always_a_create()
	{
		Assert.Equal(UpsertAction.Create, Assert.Single(CommentSync.Plan(
			[new ExistingComment(1, "anything")],
			["no marker here, just text"])).Action);
		Assert.Null(CommentSync.MarkerOf("plain text"));
	}

	[Fact]
	public void PersonaIdOf_extracts_the_id_from_the_marker()
	{
		Assert.Equal("bug-hunter", CommentSync.PersonaIdOf(CommentRenderer.Marker("bug-hunter") + "\nbody"));
		Assert.Null(CommentSync.PersonaIdOf("plain text, no marker"));
	}
}
