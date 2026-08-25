# Contributing

Install the .NET SDK selected by `global.json`, Node.js 24 or newer, and Docker or Podman. Restore the repository tools and run the full local build:

```bash
dotnet tool restore
./build.sh
```

Changes to container behavior or either sample should also run the live smoke tests:

```bash
./build.sh --containers
```

The TypeScript AppHost SDK is generated from the NuGet integration. Regenerate it with:

```bash
dotnet tool run aspire -- restore \
  --apphost samples/TypeScript/apphost.mts \
  --non-interactive
```

Do not edit `.aspire/modules` directly.

Use Conventional Commits for commit messages. Pull requests should explain the behavior being changed, include tests for public API changes, and keep both samples working.

Please report security vulnerabilities privately through GitHub's security advisory interface instead of opening a public issue.
