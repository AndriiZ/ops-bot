#!/usr/bin/env bash
# Build the release artifact of ops-bot (the standard of services, README.md §3).
# Run it on the owner machine (Git Bash or Linux) from any directory of the repository:
#   deploy/build.sh [--rid linux-x64|linux-arm64] [--skip-tests] [--allow-dirty] [--upload HOST]
# The result: build/artifacts/ops-bot-<VERSION>.tar.gz and .sha256. VERSION is <Version of OpsBot.csproj>.<number of commits>.
# --upload copies both files to HOST:/opt/ops-bot/artifacts/ with the ssh access of the owner.
# Then deploy from the bot, or on the host: sudo /opt/ops-bot/bin/deploy.sh /opt/ops-bot/artifacts/ops-bot-<VERSION>.tar.gz
set -euo pipefail

ID=ops-bot
PROJECT=src/OpsBot/OpsBot.csproj
ENTRY=OpsBot
# The files of the release: CHANGES.json has only the commits that change them.
PATHS=(src deploy template Directory.Build.props Directory.Packages.props global.json)

rid=linux-x64
tests=1
allow_dirty=0
upload=
while (( $# )); do
    case $1 in
        --rid) rid=${2:?}; shift 2 ;;
        --skip-tests) tests=0; shift ;;
        --allow-dirty) allow_dirty=1; shift ;;
        --upload) upload=${2:?}; shift 2 ;;
        *) echo "Використання: build.sh [--rid linux-x64|linux-arm64] [--skip-tests] [--allow-dirty] [--upload HOST]" >&2; exit 1 ;;
    esac
done

root=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
cd "$root"

# A version is one commit: a build with changes that are not committed would get the version of another build.
if [[ -n $(git status --porcelain -- "${PATHS[@]}") ]] && (( !allow_dirty )); then
    echo "build.sh: є незакомічені зміни; закомітьте їх або додайте --allow-dirty (лише для перевірки)" >&2
    exit 1
fi

base=$(dotnet msbuild "$PROJECT" -getProperty:Version | tr -d '\r\n')
version=$base.$(git rev-list --count HEAD)
commit=$(git rev-parse HEAD)
echo "Версія $version ($rid)"

if (( tests )); then
    dotnet test
fi

stage=build/stage/$ID
rm -rf -- "$stage"
dotnet publish "$PROJECT" --configuration Release --runtime "$rid" --self-contained --output "$stage" \
    -p:Version="$version" -p:SourceRevisionId="$commit"
printf '%s\n%s\n%s\n' "$version" "$commit" "$(date -u +%FT%TZ)" > "$stage/VERSION"
bash template/changes.sh "$version" "$stage/CHANGES.json" "${PATHS[@]}"
mkdir -p "$stage/deploy/bin" "$stage/deploy/systemd"
cp template/deploy.sh deploy/bin/ops-runner.sh "$stage/deploy/bin/"
cp deploy/systemd/* "$stage/deploy/systemd/"
[[ -f $stage/$ENTRY ]] || { echo "build.sh: немає $stage/$ENTRY" >&2; exit 1; }

# VERSION and CHANGES.json go first: ops-bot reads them without the full archive.
out=build/artifacts
file=$ID-$version.tar.gz
mkdir -p "$out"
(cd "$stage" && { echo VERSION; echo CHANGES.json; find . -mindepth 1 -maxdepth 1 ! -name VERSION ! -name CHANGES.json -printf '%P\n'; }) \
    | tar -czf "$out/$file" -C "$stage" -T -
(cd "$out" && sha256sum "$file" > "$file.sha256")
echo "Артефакт: $out/$file"

if [[ -n $upload ]]; then
    remote=${OPS_SERVICES_ROOT:-/opt}/$ID/artifacts
    # The .sha256 file goes last: the bot shows an artifact only when the file .sha256 exists.
    scp -q "$out/$file" "$upload:$remote/"
    scp -q "$out/$file.sha256" "$upload:$remote/"
    echo "Завантажено на $upload:$remote/. Розгорніть з бота або: sudo $(dirname "$remote")/bin/deploy.sh $remote/$file"
fi
