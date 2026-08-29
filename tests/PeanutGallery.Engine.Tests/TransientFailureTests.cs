using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class TransientFailureTests
{
	[Fact]
	public void Timeout_is_retryable()
	{
		// A per-attempt TimeBox deadline elapsing is the common flake — a hung route worth re-routing.
		Assert.True(TransientFailure.IsRetryable(new TimeoutException("operation exceeded 240s")));
	}

	[Fact]
	public void Outer_cancellation_is_never_retryable()
	{
		Assert.False(TransientFailure.IsRetryable(new OperationCanceledException()));
		Assert.False(TransientFailure.IsRetryable(new TaskCanceledException()));
	}

	[Theory]
	[InlineData(typeof(HttpRequestException))]
	[InlineData(typeof(SocketException))]
	[InlineData(typeof(IOException))]
	public void Transport_faults_are_retryable(Type exceptionType)
	{
		var e = (Exception)Activator.CreateInstance(exceptionType)!;
		Assert.True(TransientFailure.IsRetryable(e));
	}

	[Fact]
	public void Configuration_and_auth_failures_are_fatal()
	{
		// Missing key / unknown provider / bad request — identical on every attempt, so never retry.
		Assert.False(TransientFailure.IsRetryable(new InvalidOperationException("missing API key")));
		Assert.False(TransientFailure.IsRetryable(new ArgumentException("bad model id")));
	}

	[Fact]
	public void An_unknown_finish_reason_from_the_sdk_is_retryable()
	{
		// OpenRouter surfaces an upstream generation failure as finish_reason:"error"; the OpenAI SDK
		// rejects it as an unknown ChatFinishReason and throws while parsing. That is a transient
		// upstream error - re-issue so OpenRouter re-routes. (#113)
		var e = new ArgumentOutOfRangeException("value", "error", "Unknown ChatFinishReason value.");
		Assert.True(TransientFailure.IsRetryable(e));

		// Also when wrapped by an outer aggregate/translation.
		var wrapped = new InvalidOperationException("model call failed", e);
		Assert.True(TransientFailure.IsRetryable(wrapped));
	}

	[Fact]
	public void A_plain_argument_error_without_the_finish_reason_signature_stays_fatal()
	{
		// The finish-reason match must be narrow: a genuine bad-argument failure (e.g. an invalid
		// model id) is identical on every attempt and must NOT be retried.
		Assert.False(TransientFailure.IsRetryable(new ArgumentException("value 'gpt-9' is not a known model")));
		Assert.False(TransientFailure.IsRetryable(new ArgumentOutOfRangeException("temperature", 5.0, "out of range")));
	}

	[Fact]
	public void A_reply_the_sdk_could_not_map_is_retryable()
	{
		// A completion carrying no choices: the SDK throws out-of-range on 'index' while mapping the
		// reply, before any usage is metered, and the call boundary re-raises it as a type. That
		// route returned nothing usable, so re-issue and let OpenRouter re-route. (#158)
		var malformed = new MalformedResponseException("no choices", new ArgumentOutOfRangeException("index"));
		Assert.True(TransientFailure.IsRetryable(malformed));
		Assert.True(TransientFailure.IsMalformedResponse(malformed));

		// And once retries are exhausted the cause is wrapped - the predicate still sees through it,
		// which is what lets ReviewRunner tag the class structurally on the way out.
		Assert.True(TransientFailure.IsMalformedResponse(
			new ModelCallException("no choices (after 2 attempts)", malformed, 2)));
	}

	[Fact]
	public void The_sdk_mapping_shape_is_only_a_signal_at_the_call_boundary()
	{
		// The shape match answers "did the SDK fail to map this reply", and is only ever asked where
		// the SDK's mapping is the only code in scope (ChatClientReviewer). A parameter name is not
		// evidence of ORIGIN, so it must not classify on its own: an out-of-range 'index' arriving
		// from anywhere else is a bug of ours, identical on every attempt. Retrying it would burn the
		// budget, and calling it MalformedResponse would blame the provider for our own defect.
		var shape = new ArgumentOutOfRangeException("index");
		Assert.True(TransientFailure.IsSdkMappingFailure(shape));
		Assert.False(TransientFailure.IsRetryable(shape));
		Assert.False(TransientFailure.IsMalformedResponse(shape));

		// And the shape itself stays narrow: our own out-of-range arguments are not even candidates.
		Assert.False(TransientFailure.IsSdkMappingFailure(new ArgumentOutOfRangeException("temperature", 5.0, "out of range")));
		Assert.False(TransientFailure.IsSdkMappingFailure(null));
		Assert.False(TransientFailure.IsMalformedResponse(null));
	}

	[Fact]
	public void A_wrapped_transient_cause_is_retryable()
	{
		// e.g. an aggregate/wrapper around a dropped connection.
		var wrapped = new InvalidOperationException("call failed", new SocketException());
		Assert.True(TransientFailure.IsRetryable(wrapped));
	}

	[Fact]
	public void Null_is_not_retryable()
	{
		Assert.False(TransientFailure.IsRetryable(null));
	}
}
