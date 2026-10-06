#requires -Version 7.0
[CmdletBinding()]
param([switch] $VerifyOnly, [string] $ArtifactDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$releaseRoot = Split-Path -Parent $PSScriptRoot
$releaseMetadata = Get-Content -LiteralPath (Join-Path $releaseRoot 'RELEASE_VERSION.json') -Raw | ConvertFrom-Json
$releaseVersion = $releaseMetadata.version
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+-rc\.\d+$' -or $releaseMetadata.channel -cne 'release-candidate') {
    throw 'This publisher only accepts an explicit release-candidate version.'
}
$releaseTag = 'v' + $releaseVersion
$releaseNotes = Join-Path $releaseRoot ('docs/release-notes-' + $releaseVersion + '.md')
if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) { throw 'Version-specific release notes are missing.' }

function Invoke-ReleaseGh([string[]] $Arguments) {
    $releaseOutput = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('GitHub operation failed: ' + ($Arguments[0..([Math]::Min(1, $Arguments.Count - 1))] -join ' ')) }
    return $releaseOutput
}
function Get-ReleaseHash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Expand-CheckedArchive([string] $Path, [string] $Destination) {
    $releaseZip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $releaseEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($releaseEntry in $releaseZip.Entries) {
            $releaseName = $releaseEntry.FullName
            if (-not $releaseName.StartsWith('AnalogKeyMapper/', [StringComparison]::Ordinal) -or
                $releaseName.Contains('\') -or $releaseName.Contains(':') -or $releaseName.Contains('//') -or
                $releaseName -match '(^|/)(\.|\.\.|\.git)(/|$)' -or -not $releaseEntries.Add($releaseName) -or
                (($releaseEntry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) {
                throw 'The package contains an unsafe or duplicate archive entry.'
            }
        }
    } finally { $releaseZip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($Path, $Destination)
    return Join-Path $Destination 'AnalogKeyMapper'
}

if (-not $VerifyOnly) {
    if ($ArtifactDirectory) { throw 'Publishing only accepts the checked artifact downloaded from this workflow run.' }
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_EVENT_NAME -cne 'push' -or
        $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_REPOSITORY -notmatch '^[\w.-]+/[\w.-]+$' -or
        $env:GITHUB_SHA -notmatch '^[0-9a-f]{40}$' -or $env:GITHUB_RUN_ID -notmatch '^\d+$' -or -not $env:GH_TOKEN) {
        throw 'Publishing requires the authorized push/main GitHub Actions environment.'
    }
    $releaseHead = & git -C $releaseRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $releaseHead -cne $env:GITHUB_SHA) { throw 'Checkout does not match the checked workflow commit.' }
    $releaseSubject = & git -C $releaseRoot log -1 --format=%s
    if ($LASTEXITCODE -ne 0 -or -not $releaseSubject.StartsWith(('Release ' + $releaseVersion + ' '), [StringComparison]::Ordinal)) {
        throw 'The commit subject must explicitly request this exact release version.'
    }
    $releaseRepo = $env:GITHUB_REPOSITORY
    $env:GH_PROMPT_DISABLED = '1'
    $env:GH_NO_UPDATE_NOTIFIER = '1'
    $releaseRun = (Invoke-ReleaseGh -Arguments @('api', "repos/$releaseRepo/actions/runs/$env:GITHUB_RUN_ID")) | ConvertFrom-Json
    if ($releaseRun.head_sha -cne $env:GITHUB_SHA -or $releaseRun.event -cne 'push' -or $releaseRun.head_branch -cne 'main') {
        throw 'The checked workflow run belongs to a different source commit.'
    }
    $releaseArtifactList = (Invoke-ReleaseGh -Arguments @('api', "repos/$releaseRepo/actions/runs/$env:GITHUB_RUN_ID/artifacts?per_page=100")) | ConvertFrom-Json
    $releaseArtifacts = @($releaseArtifactList.artifacts | Where-Object name -CEQ 'AnalogKeyMapper-checked-packages')
    if ($releaseArtifacts.Count -ne 1 -or $releaseArtifacts[0].expired -or
        $releaseArtifacts[0].workflow_run.head_sha -cne $env:GITHUB_SHA) { throw 'The exact checked package artifact is unavailable.' }
    $ArtifactDirectory = Join-Path $env:RUNNER_TEMP ('checked-release-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null
    Invoke-ReleaseGh -Arguments @('run', 'download', $env:GITHUB_RUN_ID, '--repo', $releaseRepo,
        '--name', 'AnalogKeyMapper-checked-packages', '--dir', $ArtifactDirectory) | Out-Null
} elseif (-not $ArtifactDirectory) { throw 'VerifyOnly requires ArtifactDirectory.' }

$releaseAssetNames = @(('AnalogKeyMapper-' + $releaseVersion + '-source.zip'), ('AnalogKeyMapper-' + $releaseVersion + '-windows-x64.zip'), 'SHA256SUMS.txt')
$releaseFiles = @(Get-ChildItem -LiteralPath $ArtifactDirectory -File -Recurse)
if ($releaseFiles.Count -ne 3 -or @($releaseFiles | Select-Object -ExpandProperty DirectoryName -Unique).Count -ne 1 -or
    @(Compare-Object -ReferenceObject $releaseAssetNames -DifferenceObject @($releaseFiles.Name) -CaseSensitive).Count -ne 0) {
    throw 'The artifact must contain exactly the two versioned ZIP files and their SHA256SUMS.txt.'
}
$releaseAssetsDirectory = $releaseFiles[0].DirectoryName
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $releaseAssetsDirectory
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $releaseAssetsDirectory -BuildOutput -VerifyExisting
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $releaseRoot
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $releaseRoot -VerifyExisting
$releaseSourceHash = Get-ReleaseHash (Join-Path $releaseRoot 'SHA256SUMS.txt')
$releaseVerificationRoot = Join-Path ([IO.Path]::GetTempPath()) ('analog-key-mapper-release-' + [Guid]::NewGuid().ToString('N'))
$releaseSource = Expand-CheckedArchive (Join-Path $releaseAssetsDirectory $releaseAssetNames[0]) (Join-Path $releaseVerificationRoot 'source')
$releaseWindows = Expand-CheckedArchive (Join-Path $releaseAssetsDirectory $releaseAssetNames[1]) (Join-Path $releaseVerificationRoot 'windows')
foreach ($releaseDirectory in @($releaseSource, $releaseWindows)) {
    & (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $releaseDirectory
    & (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $releaseDirectory -BuildOutput -VerifyExisting
    if ((Get-ReleaseHash (Join-Path $releaseDirectory 'RELEASE_VERSION.json')) -cne (Get-ReleaseHash (Join-Path $releaseRoot 'RELEASE_VERSION.json'))) {
        throw 'Packaged version metadata differs from the checked source.'
    }
}
if ((Get-ReleaseHash (Join-Path $releaseSource 'SHA256SUMS.txt')) -cne $releaseSourceHash) {
    throw 'The source package does not contain this exact checked source manifest.'
}
$releaseExecutableNames = @('AnalogKeyMapper.exe', 'Tk75Monitor.exe', 'Tk75Diag.exe', 'ViiperOutputHost.exe')
foreach ($releaseReceiptName in @('BUILD-RECEIPT.json', 'BUILD-INFO.json')) {
    $releaseReceipt = Get-Content -LiteralPath (Join-Path $releaseWindows $releaseReceiptName) -Raw | ConvertFrom-Json
    if ($releaseReceipt.version -cne $releaseVersion -or $releaseReceipt.sourceManifestSha256 -cne $releaseSourceHash -or @($releaseReceipt.executables).Count -ne 4) {
        throw 'Packaged build provenance does not match the checked source.'
    }
    if ($releaseReceiptName -ceq 'BUILD-INFO.json' -and $releaseReceipt.channel -cne $releaseMetadata.channel) { throw 'Packaged release channel differs from the checked source.' }
    $releaseSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($releaseExecutable in $releaseReceipt.executables) {
        if ($releaseExecutableNames -cnotcontains $releaseExecutable.name -or -not $releaseSeen.Add($releaseExecutable.name) -or
            (Get-ReleaseHash (Join-Path $releaseWindows $releaseExecutable.name)) -cne $releaseExecutable.sha256) {
            throw 'A packaged executable does not match its build receipt.'
        }
    }
}
foreach ($releaseExecutableName in $releaseExecutableNames[0..2]) {
    $releaseVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $releaseWindows $releaseExecutableName))
    if ($releaseVersionInfo.FileVersion -cne $releaseMetadata.fileVersion -or $releaseVersionInfo.ProductVersion -cne $releaseVersion) {
        throw 'A packaged executable has a stale or mismatched version.'
    }
}
Write-Output ('PASS: exact checked source, metadata, manifests and executable receipts for ' + $releaseVersion + '.')
if ($VerifyOnly) { return }

$releaseRefs = (Invoke-ReleaseGh -Arguments @('api', "repos/$releaseRepo/git/matching-refs/tags/$releaseTag")) | ConvertFrom-Json
if (@($releaseRefs | Where-Object ref -CEQ "refs/tags/$releaseTag").Count -ne 0) { throw 'The release tag already exists; it will never be moved or reused.' }
$releaseExistingTags = @(Invoke-ReleaseGh -Arguments @('api', "repos/$releaseRepo/releases?per_page=100", '--paginate', '--jq', '.[].tag_name'))
if ($releaseExistingTags -ccontains $releaseTag) { throw 'A release or draft already exists; existing assets will never be overwritten.' }
$releaseAssets = @($releaseAssetNames | ForEach-Object { Join-Path $releaseAssetsDirectory $_ })
$releaseId = $null
try {
    Invoke-ReleaseGh -Arguments @('api', '--method', 'POST', "repos/$releaseRepo/git/refs", '-f', "ref=refs/tags/$releaseTag", '-f', "sha=$env:GITHUB_SHA") | Out-Null
    Invoke-ReleaseGh -Arguments (@('release', 'create', $releaseTag, '--repo', $releaseRepo, '--verify-tag', '--draft', '--prerelease',
        '--title', ('Analog Key Mapper ' + $releaseVersion), '--notes-file', $releaseNotes) + $releaseAssets) | Out-Null
    $releaseDraftIds = @(Invoke-ReleaseGh -Arguments @('api', "repos/$releaseRepo/releases?per_page=100", '--paginate', '--jq',
        ('.[] | select(.tag_name == "' + $releaseTag + '") | .id')))
    if ($releaseDraftIds.Count -ne 1 -or $releaseDraftIds[0] -notmatch '^\d+$') { throw 'The newly created draft could not be uniquely identified.' }
    $releaseId = $releaseDraftIds[0]
    $releaseDraft = (Invoke-ReleaseGh -Arguments @('api', "repos/$releaseRepo/releases/$releaseId")) | ConvertFrom-Json
    if (-not $releaseDraft.draft -or -not $releaseDraft.prerelease -or @($releaseDraft.assets).Count -ne 3) { throw 'Unexpected draft state.' }
    $releaseUploaded = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($releaseAsset in $releaseDraft.assets) {
        if ($releaseAssetNames -cnotcontains $releaseAsset.name -or -not $releaseUploaded.Add($releaseAsset.name) -or
            $releaseAsset.state -cne 'uploaded' -or $releaseAsset.digest -cne ('sha256:' + (Get-ReleaseHash (Join-Path $releaseAssetsDirectory $releaseAsset.name)))) {
            throw 'An uploaded asset differs from the checked package.'
        }
    }
    $releaseRef = (Invoke-ReleaseGh -Arguments @('api', "repos/$releaseRepo/git/ref/tags/$releaseTag")) | ConvertFrom-Json
    if ($releaseRef.object.type -cne 'commit' -or $releaseRef.object.sha -cne $env:GITHUB_SHA) { throw 'The release tag does not point to the checked commit.' }
    Invoke-ReleaseGh -Arguments @('api', '--method', 'PATCH', "repos/$releaseRepo/releases/$releaseId", '-F', 'draft=false', '-F', 'prerelease=true') | Out-Null
    $releasePublished = (Invoke-ReleaseGh -Arguments @('api', "repos/$releaseRepo/releases/$releaseId")) | ConvertFrom-Json
    if ($releasePublished.draft -or -not $releasePublished.prerelease -or $releasePublished.id -ne $releaseId) { throw 'Published release could not be verified.' }
    Write-Output ('Published checked release: ' + $releasePublished.html_url)
    ('Published [' + $releaseTag + '](' + $releasePublished.html_url + ') from `' + $env:GITHUB_SHA + '` after all offline checks and package verification.') >> $env:GITHUB_STEP_SUMMARY
} catch {
    $releaseRecovery = "Publication stopped. Inspect tag $releaseTag and release/draft ID '$releaseId' in $releaseRepo for commit $env:GITHUB_SHA. Existing tags and assets were not replaced. If a draft remains, compare its three asset SHA-256 digests with workflow run $env:GITHUB_RUN_ID before publishing it manually; do not rerun with overwrite options."
    Write-Warning $releaseRecovery
    $releaseRecovery >> $env:GITHUB_STEP_SUMMARY
    throw
}
