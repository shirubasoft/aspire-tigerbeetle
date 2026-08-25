param(
    [switch] $Containers,
    [string] $PackageVersion
)

$ErrorActionPreference = "Stop"

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Npm {
    & npm @args
    if ($LASTEXITCODE -ne 0) {
        throw "npm $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-AspireSample {
    param(
        [Parameter(Mandatory)]
        [string] $AppHost,
        [Parameter(Mandatory)]
        [string] $WaitResource
    )

    Invoke-DotNet tool run aspire -- start `
        --apphost $AppHost `
        --isolated `
        --non-interactive

    $smokeFailure = $null
    try {
        Invoke-DotNet tool run aspire -- wait $WaitResource `
            --apphost $AppHost `
            --timeout 180 `
            --non-interactive

        $descriptionLines = Invoke-DotNet tool run aspire -- describe $WaitResource `
            --apphost $AppHost `
            --format Json `
            --non-interactive
        $description = ($descriptionLines -join [Environment]::NewLine) | ConvertFrom-Json
        $clientUrl = $description.resources[0].urls |
            Where-Object { $_.name -eq "http" } |
            Select-Object -First 1 -ExpandProperty url
        if ([string]::IsNullOrWhiteSpace($clientUrl)) {
            throw "The $WaitResource resource does not expose an HTTP endpoint."
        }

        $response = Invoke-WebRequest -Uri "$($clientUrl.TrimEnd('/'))/accounts/1"
        if ($response.Content.Trim() -ne "[]") {
            throw "Expected $WaitResource /accounts/1 to return [], received: $($response.Content)"
        }
    }
    catch {
        $smokeFailure = $_
    }
    finally {
        try {
            Invoke-DotNet tool run aspire -- stop `
                --apphost $AppHost `
                --non-interactive
        }
        catch {
            if ($null -eq $smokeFailure) {
                throw
            }
        }
    }

    if ($null -ne $smokeFailure) {
        throw $smokeFailure
    }
}

function Test-ComposePublish {
    $publishOutput = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid())
    Invoke-DotNet tool run aspire -- publish `
        --apphost samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.AppHost/Aspire.TigerBeetle.Sample.CSharp.AppHost.csproj `
        --output-path $publishOutput `
        --non-interactive

    $composeFile = Join-Path $publishOutput "docker-compose.yaml"
    if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) {
        throw "Aspire publish did not create $composeFile."
    }

    $compose = Get-Content -LiteralPath $composeFile -Raw
    $requiredFragments = @(
        "ghcr.io/tigerbeetle/tigerbeetle:",
        "tigerbeetle format",
        "tigerbeetle start",
        "seccomp=unconfined",
        "volumes:"
    )
    foreach ($fragment in $requiredFragments) {
        if (-not $compose.Contains($fragment, [StringComparison]::Ordinal)) {
            throw "$composeFile does not contain '$fragment'."
        }
    }
}

$agentGuide = Get-Item -LiteralPath "AGENTS.md" -ErrorAction SilentlyContinue
if ($null -eq $agentGuide -or $agentGuide.Length -eq 0) {
    throw "AGENTS.md must exist and contain repository guidance."
}

$versionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
    $versionArguments += "-p:Version=$PackageVersion"
    $versionArguments += "-p:PackageVersion=$PackageVersion"
}

Invoke-DotNet tool restore
Invoke-DotNet restore Aspire.TigerBeetle.slnx
Invoke-DotNet tool run aspire -- restore `
    --apphost samples/TypeScript `
    --non-interactive
Invoke-Npm ci --prefix samples/TypeScript
Invoke-Npm ci --prefix samples/TypeScript/client
Invoke-Npm run build --prefix samples/TypeScript
Invoke-DotNet format Aspire.TigerBeetle.slnx --verify-no-changes --no-restore
Invoke-DotNet build Aspire.TigerBeetle.slnx --configuration Release --no-restore @versionArguments
Invoke-DotNet test Aspire.TigerBeetle.slnx --configuration Release --no-build --no-restore
Invoke-DotNet pack src/Aspire.Hosting.TigerBeetle/Aspire.Hosting.TigerBeetle.csproj `
    --configuration Release --no-build --no-restore --output artifacts @versionArguments

if ($Containers) {
    Invoke-AspireSample `
        -AppHost samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.AppHost/Aspire.TigerBeetle.Sample.CSharp.AppHost.csproj `
        -WaitResource client
    Invoke-AspireSample -AppHost samples/TypeScript -WaitResource client
    Test-ComposePublish
}
