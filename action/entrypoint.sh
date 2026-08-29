#!/bin/sh
# Container-action entrypoint. action.yml's runs.env maps inputs + provider keys to
# these env names; GITHUB_REPOSITORY / GITHUB_API_URL are provided by the Actions runtime.
set -eu

config="${PG_CONFIG:-}"
[ -n "$config" ] || config="/peanut/default.json"

# Generic provider keys: one KEY=VALUE per line from the `provider-keys` action input.
# Export each so any provider's apiKeyEnv (NVIDIA_API_KEY, TOGETHER_API_KEY, ...) is
# satisfied without a per-vendor input. Blank lines and #comments are skipped; the key
# name is trimmed, the value is taken verbatim after the first '='. (openrouter-api-key
# / fireworks-api-key remain mapped directly in action.yml.) Runs in this shell (heredoc,
# not a pipe) so the exports survive into the exec below.
if [ -n "${PG_PROVIDER_KEYS:-}" ]; then
  while IFS= read -r line; do
    line=$(printf '%s' "$line" | tr -d '\r')
    case "$line" in ''|\#*) continue ;; esac
    name=$(printf '%s' "${line%%=*}" | tr -d '[:space:]')
    [ -n "$name" ] && export "$name=${line#*=}"
  done <<PG_KEYS_EOF
$PG_PROVIDER_KEYS
PG_KEYS_EOF
fi

exec dotnet /app/peanut-gallery.dll review-pr --pr "${PG_PR_NUMBER:-}" --config "$config"
