namespace PeanutGallery.Cli;

internal static class Help
{
	public static void Print()
	{
		Console.WriteLine(
			"""
			peanut-gallery - persona-driven, multi-model code review

			Usage:
			  peanut-gallery <command> [options]

			Commands:
			  init [path]            Write a sample peanut.json (default: ./peanut.json)
			  personas              List the configured reviewer personas
			  validate              Check the config for structural problems
			  plan                  Show which personas would review a repo's diff
			  review                Run the review against real models (--dry-run for offline)
			  review-pr             Review a GitHub PR and post per-persona comments
			  await-review          Block until this push's review lands, then print its findings
			  metrics               Aggregate the per-PR run-metrics ledgers into a dogfooding report

			Options:
			  --config <path>       Config file (default: ./peanut.json)
			  --repo <name>         Repo target name / persona panel (plan/review/review-pr)
			  --diff <file|->       Unified diff file, or '-' for stdin (plan/review[-pr])
			  --pr <number>         Pull request number (review-pr, await-review)
			  --slug <owner/name>   GitHub repo (review-pr; defaults to $GITHUB_REPOSITORY)
			  --token <token>       GitHub token (review-pr; defaults to $GITHUB_TOKEN)
			  --timeout <seconds>   Give up waiting after this long (await-review; default 900)
			  --interval <seconds>  Seconds between polls (await-review; default 10)
			  --check <name>        Status check to wait on (await-review; default 'review')
			  --sha <sha>           Commit the review must report (await-review; default the PR head)
			  --dry-run             Review with the offline stub; print, do not post
			  --preview             Review with REAL models; print, do not post (review-pr)
			  --since <days>        Aggregate runs from the last N days (metrics; default 30, 0 = all)
			  --state <o|c|all>     PR state to scrape: open/closed/all, default all (metrics)
			  --json                Emit machine-readable JSON (metrics: raw JSONL)
			  --force               Overwrite an existing file (init)
			  -h, --help            Show this help
			  -v, --version         Show the version

			Examples:
			  peanut-gallery init
			  git diff origin/main... | peanut-gallery plan --repo peanut-gallery --diff -
			  peanut-gallery review --repo peanut-gallery --diff change.patch --json
			  peanut-gallery review-pr --config peanut.ci.json --pr 42   # in CI; reads env
			  peanut-gallery await-review --pr 42 --slug owner/repo      # wait for this push's review
			  peanut-gallery metrics --slug owner/repo --since 14        # dogfooding report

			await-review exit codes:
			  0  the whole panel reported, no findings   3  timed out waiting
			  2  the review landed with findings         4  the review did not happen: the job
			                                                failed, or the panel lost a reviewer
			                                                (an empty board is not a clean review
			                                                if a lens never looked)
			""");
	}
}
