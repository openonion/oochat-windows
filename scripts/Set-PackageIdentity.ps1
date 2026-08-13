<#
.SYNOPSIS
    Stamps the release version and signing publisher into Package.appxmanifest.

.DESCRIPTION
    The committed manifest carries production identity values and a deliberately invalid version
    (`0.0.0.0`). This script writes the real values in immediately before a packaged build:

      Version    from the `v*` tag, as MSIX requires Major.Minor.Build.Revision with Revision 0
                 (Revision is reserved for the Store; a package that sets it cannot be submitted).
      Publisher  from the signing certificate's subject, which must match byte for byte or the
                 package will not install.

    `Name` is never touched. Together with Publisher it is the upgrade identity — see the comment
    in the manifest and docs/RELEASE.md.

    Intended to run against a disposable CI working tree. `-Restore` puts the file back for local
    use, and the script writes a `.orig` sidecar so that is always possible.

.PARAMETER Version
    Semantic version, with or without a leading `v` (`v1.2.3`, `1.2.3`). Prerelease suffixes are
    accepted and dropped from the MSIX version, which has no way to express them.

.PARAMETER PublisherDistinguishedName
    The certificate subject, e.g. `CN=ConnectOnion, O=ConnectOnion, C=AU`. Left alone if omitted.

.PARAMETER ManifestPath
    Defaults to ConnectOnion.WinUIClient/Package.appxmanifest.

.PARAMETER Restore
    Restore the manifest from the `.orig` sidecar and delete it.

.EXAMPLE
    pwsh scripts/Set-PackageIdentity.ps1 -Version v1.2.3 -PublisherDistinguishedName 'CN=ConnectOnion'
.EXAMPLE
    pwsh scripts/Set-PackageIdentity.ps1 -Restore
#>
[CmdletBinding(DefaultParameterSetName = 'Set')]
param(
    [Parameter(ParameterSetName = 'Set', Mandatory = $true)]
    [string] $Version,

    [Parameter(ParameterSetName = 'Set')]
    [string] $PublisherDistinguishedName,

    [string] $ManifestPath,

    [Parameter(ParameterSetName = 'Restore', Mandatory = $true)]
    [switch] $Restore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ManifestPath) {
    $ManifestPath = Join-Path $RepoRoot 'ConnectOnion.WinUIClient/Package.appxmanifest'
}
if (-not (Test-Path $ManifestPath)) { throw "Manifest not found at $ManifestPath." }
$backupPath = "$ManifestPath.orig"

if ($Restore) {
    if (-not (Test-Path $backupPath)) {
        Write-Host "No $backupPath to restore from; manifest left as is."
        exit 0
    }
    Move-Item -Force $backupPath $ManifestPath
    Write-Host "Restored $ManifestPath."
    exit 0
}

# --- version ---------------------------------------------------------------------------------

# Tolerates `v1.2.3`, `1.2.3`, and `1.2.3-rc.1`. The prerelease part is captured only so it can
# be reported; MSIX versions are four integers and cannot carry it, which means two prereleases
# of the same version are indistinguishable to Windows. docs/RELEASE.md says not to ship those.
if ($Version -notmatch '^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?<pre>-[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a semantic version (expected v1.2.3 or 1.2.3)."
}

$major = [int]$Matches.major
$minor = [int]$Matches.minor
$patch = [int]$Matches.patch
$prerelease = if ($Matches.ContainsKey('pre')) { $Matches.pre } else { '' }

foreach ($part in @($major, $minor, $patch)) {
    if ($part -gt 65535) { throw "Version component $part exceeds the MSIX limit of 65535." }
}
if ($major -eq 0 -and $minor -eq 0 -and $patch -eq 0) {
    throw "0.0.0 is the placeholder that marks an unstamped manifest; it cannot be a release version."
}

# Revision stays 0: the Store reserves it, and a package that sets it is rejected on submission.
$packageVersion = "$major.$minor.$patch.0"

# --- rewrite ---------------------------------------------------------------------------------

if (-not (Test-Path $backupPath)) { Copy-Item $ManifestPath $backupPath }

# Text substitution rather than XML round-tripping on purpose: [xml] rewrites the declaration,
# reorders nothing but reformats everything, and drops the comments that explain why the identity
# is immutable — turning a two-attribute edit into an unreviewable whole-file diff.
$content = Get-Content -Raw -LiteralPath $ManifestPath

<#
    Substitutes one Identity attribute, and verifies the *pattern matched* rather than that the
    text changed. Those are not the same question: stamping a value the manifest already carries
    is a legitimate no-op (re-running the script, or a publisher that happens to equal the
    committed default), and treating it as "attribute not found" turns a correct run into a
    failed build — which is exactly what it did the first time this ran twice.
#>
function Set-IdentityAttribute {
    param(
        [string] $Xml,
        [string] $Attribute,
        [string] $Value
    )

    $pattern = "(?<prefix><Identity\b[^>]*?\b$Attribute="")[^""]*(?<suffix>"")"
    if (-not [regex]::IsMatch($Xml, $pattern)) {
        throw "Could not find the Identity $Attribute attribute in $ManifestPath."
    }
    return [regex]::Replace($Xml, $pattern,
        { param($m) $m.Groups['prefix'].Value + $Value + $m.Groups['suffix'].Value })
}

$content = Set-IdentityAttribute -Xml $content -Attribute 'Version' -Value $packageVersion

if ($PublisherDistinguishedName) {
    $content = Set-IdentityAttribute -Xml $content -Attribute 'Publisher' `
        -Value ([System.Security.SecurityElement]::Escape($PublisherDistinguishedName))
}

# UTF-8 with BOM: what the file already is, and what the appx tooling expects.
[System.IO.File]::WriteAllText($ManifestPath, $content, [System.Text.UTF8Encoding]::new($true))

$identityName = if ($content -match '<Identity\b[^>]*?\bName="([^"]*)"') { $Matches[1] } else { '<unknown>' }
$publisher    = if ($content -match '<Identity\b[^>]*?\bPublisher="([^"]*)"') { $Matches[1] } else { '<unknown>' }

Write-Host "Package identity stamped:"
Write-Host "  Name      : $identityName  (upgrade identity — never changes)"
Write-Host "  Publisher : $publisher"
Write-Host "  Version   : $packageVersion"
if ($prerelease) {
    Write-Host "  NOTE      : prerelease suffix '$prerelease' dropped; MSIX cannot express it." -ForegroundColor Yellow
}

# Consumed by the release workflow so the assembly version and the package version cannot drift.
if ($env:GITHUB_OUTPUT) {
    "package_version=$packageVersion"          | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "assembly_version=$major.$minor.$patch"    | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "identity_name=$identityName"              | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
