#!/usr/bin/env bash
# Creates the maps-tools toolbox in the Foundry project of the active azd environment.
#
# Usage: scripts/create-toolbox.sh [toolbox-name] [--replace] [--render-only]
#   toolbox-name   defaults to maps-tools
#   --replace      delete an existing toolbox of that name first (versions are immutable)
#   --render-only  only write toolbox/.rendered/maps.toolbox.yaml and print its path
#
# Reads AZURE_AI_PROJECT_ID and FOUNDRY_PROJECT_ENDPOINT from `azd env get-values`, fills the
# ${AZURE_AI_PROJECT_ID} placeholder in toolbox/maps.toolbox.yaml, runs `azd ai toolbox create`,
# then sets TOOLBOX_NAME in the azd environment so `azd deploy` passes it to the container.
set -euo pipefail
cd "$(dirname "$0")/.."

name="maps-tools"
replace=0
render_only=0
for arg in "$@"; do
    case "$arg" in
        --replace) replace=1 ;;
        --render-only) render_only=1 ;;
        --*) echo "Unknown option: $arg" >&2; exit 2 ;;
        *) name="$arg" ;;
    esac
done

env_value() {
    azd env get-values | sed -n "s/^$1=\"\(.*\)\"$/\1/p"
}

project_id="$(env_value AZURE_AI_PROJECT_ID)"
endpoint="$(env_value FOUNDRY_PROJECT_ENDPOINT)"
if [ -z "$project_id" ] || [ -z "$endpoint" ]; then
    echo "Set AZURE_AI_PROJECT_ID and FOUNDRY_PROJECT_ENDPOINT in the azd environment first (azd env set ...)." >&2
    exit 1
fi

mkdir -p toolbox/.rendered
rendered="toolbox/.rendered/maps.toolbox.yaml"
sed "s|\${AZURE_AI_PROJECT_ID}|$project_id|g" toolbox/maps.toolbox.yaml > "$rendered"
echo "Rendered $rendered for $project_id"
if [ "$render_only" = 1 ]; then
    exit 0
fi

# Git Bash rewrites arguments that look like POSIX paths (the ARM id starts with /subscriptions);
# MSYS_NO_PATHCONV stops that for the azd calls below.
export MSYS_NO_PATHCONV=1
if [ "$replace" = 1 ]; then
    azd ai toolbox delete "$name" --force --project-endpoint "$endpoint" --no-prompt || true
fi
azd ai toolbox create "$name" --from-file "$rendered" --project-endpoint "$endpoint" --no-prompt
azd env set TOOLBOX_NAME "$name"
echo "TOOLBOX_NAME=$name set in the azd environment; run azd deploy next."
