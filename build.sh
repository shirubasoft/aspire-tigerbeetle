#!/usr/bin/env bash
set -euo pipefail

run_containers=false
package_version=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --containers)
      run_containers=true
      shift
      ;;
    --package-version)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--package-version requires a value." >&2
        exit 2
      fi

      package_version="$2"
      shift 2
      ;;
    *)
      echo "Usage: ./build.sh [--containers] [--package-version <version>]" >&2
      exit 2
      ;;
  esac
done

if [[ ! -s AGENTS.md ]]; then
  echo "AGENTS.md must exist and contain repository guidance." >&2
  exit 1
fi

version_arguments=()
if [[ -n "$package_version" ]]; then
  version_arguments+=(
    "-p:Version=$package_version"
    "-p:PackageVersion=$package_version"
  )
fi

dotnet tool restore
dotnet restore Aspire.TigerBeetle.slnx
dotnet tool run aspire -- restore \
  --apphost samples/TypeScript \
  --non-interactive
npm ci --prefix samples/TypeScript
npm ci --prefix samples/TypeScript/client
npm run build --prefix samples/TypeScript
dotnet format Aspire.TigerBeetle.slnx --verify-no-changes --no-restore
dotnet build Aspire.TigerBeetle.slnx --configuration Release --no-restore \
  "${version_arguments[@]}"
dotnet test Aspire.TigerBeetle.slnx --configuration Release --no-build --no-restore
dotnet pack src/Aspire.Hosting.TigerBeetle/Aspire.Hosting.TigerBeetle.csproj \
  --configuration Release --no-build --no-restore --output artifacts \
  "${version_arguments[@]}"

run_sample() {
  local apphost="$1"
  local wait_resource="$2"
  local description=""
  local client_url=""
  local response=""
  local smoke_status=0
  local wait_status=0
  local stop_status=0

  dotnet tool run aspire -- start \
    --apphost "$apphost" \
    --isolated \
    --non-interactive

  dotnet tool run aspire -- wait "$wait_resource" \
    --apphost "$apphost" \
    --timeout 180 \
    --non-interactive || wait_status=$?

  if [[ $wait_status -eq 0 ]]; then
    description="$(dotnet tool run aspire -- describe "$wait_resource" \
      --apphost "$apphost" \
      --format Json \
      --non-interactive)" || smoke_status=$?
  fi

  if [[ $wait_status -eq 0 && $smoke_status -eq 0 ]]; then
    client_url="$(jq -er \
      '.resources[0].urls[] | select(.name == "http") | .url' \
      <<<"$description")" || smoke_status=$?
  fi

  if [[ $wait_status -eq 0 && $smoke_status -eq 0 ]]; then
    response="$(curl --fail --silent --show-error \
      --retry 10 \
      --retry-connrefused \
      --retry-delay 1 \
      "${client_url%/}/accounts/1")" || smoke_status=$?
  fi

  if [[ $wait_status -eq 0 && $smoke_status -eq 0 ]] && \
    ! jq -e '. == []' <<<"$response" >/dev/null; then
    echo "Expected $wait_resource /accounts/1 to return [], received: $response" >&2
    smoke_status=1
  fi

  dotnet tool run aspire -- stop \
    --apphost "$apphost" \
    --non-interactive || stop_status=$?

  if [[ $wait_status -ne 0 ]]; then
    return "$wait_status"
  fi

  if [[ $smoke_status -ne 0 ]]; then
    return "$smoke_status"
  fi

  return "$stop_status"
}

verify_compose_publish() {
  local publish_output
  local compose_file

  publish_output="$(mktemp -d)"
  compose_file="$publish_output/docker-compose.yaml"

  dotnet tool run aspire -- publish \
    --apphost samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.AppHost/Aspire.TigerBeetle.Sample.CSharp.AppHost.csproj \
    --output-path "$publish_output" \
    --non-interactive

  test -s "$compose_file"
  grep -F 'ghcr.io/tigerbeetle/tigerbeetle:' "$compose_file" >/dev/null
  grep -F 'tigerbeetle format' "$compose_file" >/dev/null
  grep -F 'tigerbeetle start' "$compose_file" >/dev/null
  grep -F 'seccomp=unconfined' "$compose_file" >/dev/null
  grep -F 'volumes:' "$compose_file" >/dev/null
}

if [[ "$run_containers" == true ]]; then
  command -v curl >/dev/null
  command -v jq >/dev/null
  run_sample \
    samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.AppHost/Aspire.TigerBeetle.Sample.CSharp.AppHost.csproj \
    client
  run_sample samples/TypeScript client
  verify_compose_publish
fi
