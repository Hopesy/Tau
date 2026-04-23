#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${repo_root}"

skip_restore=false

for arg in "$@"; do
  case "$arg" in
    --skip-restore)
      skip_restore=true
      ;;
    *)
      echo "未知参数: $arg" >&2
      echo "用法: bash scripts/verify-dotnet.sh [--skip-restore]" >&2
      exit 1
      ;;
  esac
done

source_projects=(
  "src/Tau.Ai/Tau.Ai.csproj"
  "src/Tau.Agent/Tau.Agent.csproj"
  "src/Tau.Tui/Tau.Tui.csproj"
  "src/Tau.CodingAgent/Tau.CodingAgent.csproj"
  "src/Tau.WebUi/Tau.WebUi.csproj"
  "src/Tau.Mom/Tau.Mom.csproj"
  "src/Tau.Pods/Tau.Pods.csproj"
)

test_projects=(
  "tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj"
  "tests/Tau.Agent.Tests/Tau.Agent.Tests.csproj"
  "tests/Tau.Tui.Tests/Tau.Tui.Tests.csproj"
  "tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj"
  "tests/Tau.Pods.Tests/Tau.Pods.Tests.csproj"
)

if [[ "${skip_restore}" == false ]]; then
  echo "==> restore"
  for project in "${source_projects[@]}" "${test_projects[@]}"; do
    echo "dotnet restore ${project}"
    dotnet restore "${project}" --verbosity minimal
  done
fi

echo "==> build src"
for project in "${source_projects[@]}"; do
  echo "dotnet build ${project}"
  dotnet build "${project}" --no-restore --verbosity minimal
done

echo "==> build tests"
for project in "${test_projects[@]}"; do
  echo "dotnet build ${project}"
  dotnet build "${project}" --no-restore --verbosity minimal
done

echo "==> test"
for project in "${test_projects[@]}"; do
  echo "dotnet test ${project}"
  dotnet test "${project}" --no-build --no-restore --verbosity minimal
done

echo "Tau .NET 项目级验证通过"
