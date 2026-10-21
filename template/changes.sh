#!/usr/bin/env bash
# Write CHANGES.json of a release (the standard of services, ops-bot/README.md §3). Copy this file into the project
# without changes. build.sh runs it from the root of the repository:
#   changes.sh <VERSION> <output.json> [path...]
# A change is a commit. The text comes from the trailers of the commit message, in Ukrainian and in English:
#   Change-uk: Шаблони сповіщень банків від адміністратора
#   Change-en: Bank notification templates from the administrator
# A commit without these trailers gives its subject in both languages. "Change-uk: -" hides the commit.
# The paths limit the commits to the files of the service. Merge commits give no change.
# "build" of a change is the number of commits of the first-parent commit that brings the change into the branch.
# The last part of VERSION is the number of commits of HEAD, thus ops-bot selects the changes between two versions.
set -euo pipefail

VERSION=${1:?"версія"}
OUT=${2:?"файл CHANGES.json"}
shift 2
PATHS=("$@")
DEPTH=${CHANGES_DEPTH:-100}

# A JSON string. Commit texts have no other control characters.
json() {
    local s=$1
    s=${s//\\/\\\\}
    s=${s//\"/\\\"}
    s=${s//$'\t'/ }
    s=${s//$'\r'/}
    s=${s//$'\n'/ }
    printf '"%s"' "$s"
}

trailer() { echo "%(trailers:key=$1,valueonly,unfold,separator=%x3b%x20)"; }
FORMAT="%H%x1f%cs%x1f%s%x1f$(trailer Change-uk)%x1f$(trailer Change-en)%x1e"

entries=()
while read -r head; do
    build=$(git rev-list --count "$head")
    # A merge brings the commits of the merged branch; another commit brings only itself.
    if git rev-parse -q --verify "$head^2" >/dev/null; then range="$head^1..$head"; else range="$head^!"; fi
    while IFS=$'\x1f' read -r -d $'\x1e' sha date subject uk en; do
        sha=${sha#$'\n'}
        [[ -n $sha ]] || continue
        [[ $uk == - || $en == - ]] && continue
        uk=${uk:-$subject}
        en=${en:-$subject}
        entries+=("{\"build\":$build,\"commit\":\"${sha:0:10}\",\"date\":\"$date\",\"uk\":$(json "$uk"),\"en\":$(json "$en")}")
    done < <(git log --no-merges --format="$FORMAT" "$range" -- "${PATHS[@]}")
done < <(git rev-list --first-parent -n "$DEPTH" HEAD)

{
    printf '{"version":"%s","commit":"%s","changes":[' "$VERSION" "$(git rev-parse HEAD)"
    for i in "${!entries[@]}"; do
        (( i == 0 )) || printf ','
        printf '\n%s' "${entries[$i]}"
    done
    printf '\n]}\n'
} > "$OUT"
echo "CHANGES.json: ${#entries[@]} змін"
