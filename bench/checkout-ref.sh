#!/usr/bin/env bash
# Usage: checkout-ref.sh REPO REF DEST [WITH_REF...]
#
# Checks out REF from the clone at REPO into a new worktree at DEST, merges each WITH_REF into
# it, and replaces DEST/bench with the harness from REPO, so every build uses the same harness
# and only src/ and test/ come from the refs. A WITH_REF stacks a change the measured one
# depends on (an unmerged fix, say) onto both the head and the baseline.
set -euo pipefail

repo=$1
ref=$2
# Resolved once, so relative and absolute destinations behave the same in every command below.
dest=$(realpath -m -- "$3")
shift 3

resolve() {
    local name=$1 sha
    if [[ ! "${name}" =~ ^[A-Za-z0-9._/][A-Za-z0-9._/-]*$ ]]; then
        echo "::error::'${name}' is not a valid ref name or commit ID" >&2
        return 1
    fi
    # A local name or commit ID first, then a remote branch, then a fetch by name or full
    # commit ID (for a push whose previous tip is no longer on any branch).
    sha=$(git -C "${repo}" rev-parse --verify --quiet "${name}^{commit}") \
        || sha=$(git -C "${repo}" rev-parse --verify --quiet "origin/${name}^{commit}") \
        || { git -C "${repo}" fetch --quiet --no-tags origin "${name}" \
             && sha=$(git -C "${repo}" rev-parse --verify --quiet "FETCH_HEAD^{commit}"); } \
        || { echo "::error::cannot resolve '${name}'" >&2; return 1; }
    echo "${sha}"
}

sha=$(resolve "${ref}")
git -C "${repo}" worktree add --quiet --detach "${dest}" "${sha}"
echo "${dest}: ${ref} resolved to ${sha}"

for with in "$@"; do
    with_sha=$(resolve "${with}")
    if ! git -C "${dest}" -c user.name=bench -c user.email=bench@localhost \
            merge --quiet --no-edit "${with_sha}"; then
        echo "::error::${with} (${with_sha}) does not merge cleanly into ${ref}" >&2
        # Leave no half-merged worktree behind, so a retry for the same destination starts clean.
        git -C "${repo}" worktree remove --force "${dest}"
        exit 1
    fi
    echo "${dest}: merged ${with} at ${with_sha}"
done

rm -rf "${dest}/bench"
cp -r "${repo}/bench" "${dest}/bench"
