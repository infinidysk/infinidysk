#!/usr/bin/env bash
# Deletes versioned pre-release GitHub releases (vX.Y.Z-rc.N) and their image
# tags after a stable release. Leaves the rolling `rc` release and tag alone.
#
# Required env:
#   GH_TOKEN              — GitHub token with contents:write + packages:write
#   GITHUB_REPOSITORY     — owner/name (e.g. infinidysk/infinidysk)
#
# Optional env:
#   DOCKERHUB_USERNAME    — default: infinidysk
#   DOCKERHUB_TOKEN       — Docker Hub token (skip Hub cleanup when empty)
#   DOCKERHUB_REPO        — default: infinidysk
#   OLD_GHCR_TOKEN        — token for legacy ghcr.io/nzbdav/nzbdav (skip when empty)
#   LEGACY_GHCR_ORG       — default: nzbdav
#   LEGACY_GHCR_PACKAGE   — default: nzbdav

set -euo pipefail

REPO="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
ORG="${REPO%%/*}"
PACKAGE="${REPO##*/}"
DOCKERHUB_USERNAME="${DOCKERHUB_USERNAME:-infinidysk}"
DOCKERHUB_REPO="${DOCKERHUB_REPO:-infinidysk}"
LEGACY_GHCR_ORG="${LEGACY_GHCR_ORG:-nzbdav}"
LEGACY_GHCR_PACKAGE="${LEGACY_GHCR_PACKAGE:-nzbdav}"
RC_TAG_RE='^v[0-9]+\.[0-9]+\.[0-9]+-rc\.[0-9]+$'

if [[ -z "${GH_TOKEN:-${GITHUB_TOKEN:-}}" ]]; then
  echo "GH_TOKEN or GITHUB_TOKEN is required" >&2
  exit 1
fi
export GH_TOKEN="${GH_TOKEN:-$GITHUB_TOKEN}"

is_http_ok_or_missing() {
  local code="$1"
  [[ "$code" == "200" || "$code" == "204" || "$code" == "404" ]]
}

list_ghcr_rc_tags() {
  local org="$1"
  local package="$2"
  local token="$3"
  GH_TOKEN="$token" gh api --paginate \
    "orgs/${org}/packages/container/${package}/versions?per_page=100" \
    --jq '.[] | (.metadata.container.tags // [])[]' \
    | jq -R -r --arg re "$RC_TAG_RE" 'select(test($re))' \
    | sort -u
}

list_rc_tags() {
  {
    gh release list --repo "$REPO" --limit 1000 --json tagName,isPrerelease \
      | jq -r --arg re "$RC_TAG_RE" \
        '.[] | select(.isPrerelease and (.tagName | test($re))) | .tagName'
    list_ghcr_rc_tags "$ORG" "$PACKAGE" "$GH_TOKEN" || true
    if [[ -n "${OLD_GHCR_TOKEN:-}" ]]; then
      list_ghcr_rc_tags "$LEGACY_GHCR_ORG" "$LEGACY_GHCR_PACKAGE" "$OLD_GHCR_TOKEN" || true
    fi
  } | sort -u
}

delete_ghcr_versions_for_tags() {
  local org="$1"
  local package="$2"
  local token="$3"
  local label="$4"
  shift 4
  local tags=("$@")

  if [[ ${#tags[@]} -eq 0 ]]; then
    return 0
  fi

  echo "Scanning ${label} package versions for RC tags…"
  local versions_json
  if ! versions_json=$(
    GH_TOKEN="$token" gh api --paginate \
      "orgs/${org}/packages/container/${package}/versions?per_page=100" \
      --jq '.[] | {id, tags: (.metadata.container.tags // [])}' \
      | jq -s '.'
  ); then
    echo "Failed to list ${label} package versions" >&2
    return 1
  fi

  local tag id http_code
  local -a ids_to_delete=()
  for tag in "${tags[@]}"; do
    while IFS= read -r id; do
      [[ -z "$id" || "$id" == "null" ]] && continue
      ids_to_delete+=("$id")
    done < <(jq -r --arg tag "$tag" '.[] | select(.tags | index($tag)) | .id' <<<"$versions_json")
  done

  # Unique version IDs (a version should only carry one RC tag, but be safe).
  if [[ ${#ids_to_delete[@]} -eq 0 ]]; then
    echo "No ${label} package versions matched RC tags"
    return 0
  fi
  mapfile -t ids_to_delete < <(printf '%s\n' "${ids_to_delete[@]}" | sort -u)

  for id in "${ids_to_delete[@]}"; do
    echo "Deleting ${label} package version id=${id}"
    http_code=$(
      curl -sS -o /dev/null -w "%{http_code}" -X DELETE \
        -H "Authorization: Bearer ${token}" \
        -H "Accept: application/vnd.github+json" \
        -H "X-GitHub-Api-Version: 2022-11-28" \
        "https://api.github.com/orgs/${org}/packages/container/${package}/versions/${id}"
    )
    if ! is_http_ok_or_missing "$http_code"; then
      echo "Unexpected status ${http_code} deleting ${label} version ${id}" >&2
      return 1
    fi
  done
}

delete_dockerhub_tags() {
  local tags=("$@")
  if [[ ${#tags[@]} -eq 0 ]]; then
    return 0
  fi
  if [[ -z "${DOCKERHUB_TOKEN:-}" ]]; then
    echo "DOCKERHUB_TOKEN unset; skipping Docker Hub tag cleanup"
    return 0
  fi

  echo "Authenticating to Docker Hub…"
  local hub_token
  hub_token=$(
    curl -fsS -H "Content-Type: application/json" \
      -d "{\"username\":\"${DOCKERHUB_USERNAME}\",\"password\":\"${DOCKERHUB_TOKEN}\"}" \
      https://hub.docker.com/v2/users/login/ \
      | jq -r .token
  )
  if [[ -z "$hub_token" || "$hub_token" == "null" ]]; then
    echo "Failed to obtain Docker Hub JWT" >&2
    return 1
  fi

  local tag http_code
  for tag in "${tags[@]}"; do
    echo "Deleting docker.io/${DOCKERHUB_USERNAME}/${DOCKERHUB_REPO}:${tag}"
    http_code=$(
      curl -sS -o /dev/null -w "%{http_code}" -X DELETE \
        -H "Authorization: JWT ${hub_token}" \
        "https://hub.docker.com/v2/repositories/${DOCKERHUB_USERNAME}/${DOCKERHUB_REPO}/tags/${tag}/"
    )
    if ! is_http_ok_or_missing "$http_code"; then
      echo "Unexpected status ${http_code} deleting Docker Hub tag ${tag}" >&2
      return 1
    fi
  done
}

delete_github_releases() {
  local tags=("$@")
  local tag
  for tag in "${tags[@]}"; do
    echo "Deleting GitHub release and git tag ${tag}"
    if ! gh release view "$tag" --repo "$REPO" >/dev/null 2>&1; then
      echo "Release ${tag} already absent; skipping"
      continue
    fi
    gh release delete "$tag" --repo "$REPO" --yes --cleanup-tag
  done
}

mapfile -t RC_TAGS < <(list_rc_tags)

if [[ ${#RC_TAGS[@]} -eq 0 || -z "${RC_TAGS[0]:-}" ]]; then
  echo "No versioned pre-release artifacts (v*-rc.*) to clean up."
  exit 0
fi

echo "Found ${#RC_TAGS[@]} versioned pre-release tag(s) to delete:"
printf '  %s\n' "${RC_TAGS[@]}"

delete_ghcr_versions_for_tags "$ORG" "$PACKAGE" "$GH_TOKEN" "ghcr.io/${ORG}/${PACKAGE}" "${RC_TAGS[@]}"

if [[ -n "${OLD_GHCR_TOKEN:-}" ]]; then
  delete_ghcr_versions_for_tags \
    "$LEGACY_GHCR_ORG" "$LEGACY_GHCR_PACKAGE" "$OLD_GHCR_TOKEN" \
    "ghcr.io/${LEGACY_GHCR_ORG}/${LEGACY_GHCR_PACKAGE}" \
    "${RC_TAGS[@]}"
else
  echo "OLD_GHCR_TOKEN unset; skipping legacy GHCR tag cleanup"
fi

delete_dockerhub_tags "${RC_TAGS[@]}"
delete_github_releases "${RC_TAGS[@]}"

echo "Versioned pre-release cleanup complete."
