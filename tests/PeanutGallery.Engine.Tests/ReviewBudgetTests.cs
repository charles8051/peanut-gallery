using System;
using System.Collections.Generic;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>The env knobs, read once and totally — a garbage value is the default, never a crash.</summary>
public class ReviewBudgetTests
{
    [Theory]
    [InlineData("300", 300)]
    [InlineData(" 300 ", 300)]
    [InlineData("1", 1)]
    public void A_usable_value_is_taken(string raw, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), ReviewBudget.Parse(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("ten minutes")]
    [InlineData("300.5")]
    public void Anything_unusable_falls_back_to_the_default(string? raw) =>
        Assert.Equal(TimeSpan.FromSeconds(ReviewBudget.DefaultSeconds), ReviewBudget.Parse(raw));

    [Theory]
    [InlineData("4", 4)]
    [InlineData("0", ReviewBudget.DefaultMaxAttempts)]
    [InlineData("nope", ReviewBudget.DefaultMaxAttempts)]
    [InlineData(null, ReviewBudget.DefaultMaxAttempts)]
    public void Attempts_follow_the_same_rule(string? raw, int expected) =>
        Assert.Equal(expected, ReviewBudget.Attempts(raw));

    [Fact]
    public void Both_knobs_come_from_one_lookup()
    {
        var env = new Dictionary<string, string?>
        {
            [ReviewBudget.TimeoutVariable] = "120",
            [ReviewBudget.AttemptsVariable] = "3",
        };

        var (timeout, _, attempts, _) = ReviewBudget.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal(TimeSpan.FromSeconds(120), timeout);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void An_empty_environment_yields_the_documented_defaults()
    {
        var (timeout, callTimeout, attempts, maxOutput) = ReviewBudget.FromEnvironment(_ => null);

        Assert.Equal(TimeSpan.FromSeconds(600), timeout);
        Assert.Equal(TimeSpan.FromSeconds(300), callTimeout);
        Assert.Equal(2, attempts);
        Assert.Equal(40000, maxOutput);
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData(" 90 ", 90)]
    [InlineData(null, 300)]         // unset -> default
    [InlineData("", 300)]
    [InlineData("nonsense", 300)]
    [InlineData("0", 300)]          // non-positive -> default
    [InlineData("-5", 300)]
    public void The_per_call_timeout_reads_from_the_environment_split_from_the_turn_budget(string? raw, int expected)
    {
        // The per-call budget is nested inside the turn budget - reading one must not disturb the other.
        var (turn, call, _, _) = ReviewBudget.FromEnvironment(
            k => k == ReviewBudget.CallTimeoutVariable ? raw : null);
        Assert.Equal(TimeSpan.FromSeconds(expected), call);
        Assert.Equal(TimeSpan.FromSeconds(600), turn); // turn budget untouched by the call-timeout var
    }

    [Theory]
    [InlineData("16384", 16384)]
    [InlineData("2000", 2000)]
    [InlineData("", 40000)]        // unset -> default
    [InlineData("nonsense", 40000)]
    [InlineData("0", 40000)]       // non-positive -> default (an uncapped call is the bug being fixed)
    [InlineData("-5", 40000)]
    public void The_output_token_cap_reads_from_the_environment_with_a_generous_default(string raw, int expected)
    {
        var (_, _, _, maxOutput) = ReviewBudget.FromEnvironment(
            k => k == ReviewBudget.MaxOutputVariable ? raw : null);
        Assert.Equal(expected, maxOutput);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("Yes")]
    [InlineData(" true ")]
    public void The_fail_on_degraded_gate_is_on_only_for_an_explicit_truthy_value(string raw) =>
        Assert.True(ReviewBudget.FailOnDegraded(raw));

    [Theory]
    [InlineData(null)]          // unset -> advisory, the safe default
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("maybe")]
    public void The_fail_on_degraded_gate_is_off_by_default(string? raw) =>
        Assert.False(ReviewBudget.FailOnDegraded(raw));
}
