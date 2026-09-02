<#
.SYNOPSIS
    Runs PSScriptAnalyzer over every PowerShell file in the repository and writes the findings as SARIF.

.DESCRIPTION
    A script rather than an inline `run:` block, for the reason every other non-trivial CI step here is one:
    it can be run and debugged locally, and it is itself subject to the scan it performs - which the YAML
    version never was, being the only PowerShell in the repository the analyzer could not see.

    Findings never fail this job; they are uploaded as SARIF and surface in code scanning. The only failure
    modes are the analyzer throwing after its retries, or a file filter that matches nothing.
#>
[CmdletBinding()]
# Write-Host is deliberate: this is a CI transcript. Write-Output would put the messages in the return
# value, and Write-Verbose/Information are hidden by default - the opposite of what a scan log needs.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'Intentional CI transcript output')]
param(
    # The tree to scan. Defaults to the repository root, two levels up from .github/scripts.
    [string]$Root = (Join-Path $PSScriptRoot '..' | Join-Path -ChildPath '..' | Convert-Path),
    [string]$SarifPath = 'results.sarif',
    # How many times one file may be re-analyzed before the analyzer's own crash is treated as fatal.
    [int]$MaxAttempts = 3
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptExtensions = @('.ps1', '.psm1', '.psd1')

# One traversal, filtered on the extension afterwards. Neither -Filter nor -Include is used: -Filter takes a
# single pattern (so it would mean one full traversal per extension), and -Include against a directory -Path
# matches the path leaf and needs a trailing wildcard to behave at all.
#
# -Force is the load-bearing switch, and the reason two attempts at this found zero files on the runner while
# working locally: every script in this repository lives under .github, a DOT-PREFIXED directory. POSIX
# treats that as hidden, and Get-ChildItem -Recurse does not descend into hidden directories without -Force.
# On Windows .github carries no hidden attribute, so it is enumerated either way - the platform difference is
# in the filesystem, not in PowerShell.
$files = @(Get-ChildItem -Path $Root -Recurse -File -Force |
    Where-Object { $_.Extension -in $scriptExtensions } |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin|node_modules|\.git)[\\/]' } |
    Select-Object -ExpandProperty FullName -Unique | Sort-Object)

Write-Host "Analyzing $($files.Count) PowerShell file(s)."
# A filter that matches nothing must fail, not report a clean run: "0 findings" and "analyzed nothing" are
# indistinguishable in the SARIF upload, and the second one is how a scan quietly stops scanning.
if ($files.Count -eq 0) { throw 'No PowerShell files found - the file filter is wrong.' }

# Analyzed one file at a time, with a bounded retry.
#
# Recursing a tree makes PSScriptAnalyzer walk directories and probe for modules, and that path throws an
# intermittent "Object reference not set to an instance of an object" - reproduced here as 1 crash in 3 runs
# over an UNCHANGED tree, on 1.22.0 and 1.25.0 alike. It is a flake in the analyzer, not a finding: the same
# files analyzed individually are always clean. Passing paths directly skips the traversal that trips it, and
# the per-file loop scopes each retry to the file that flaked instead of redoing the whole set.
$results = @(foreach ($file in $files) {
    foreach ($attempt in 1..$MaxAttempts) {
        try {
            Invoke-ScriptAnalyzer -Path $file -ErrorAction Stop
            break
        }
        catch {
            Write-Host "::warning::PSScriptAnalyzer attempt $attempt on $file failed: $($_.Exception.Message)"
            if ($attempt -eq $MaxAttempts) { throw }
        }
    }
})
Write-Host "$($results.Count) finding(s)."

$sarifResults = @($results | ForEach-Object {
    $relative = $_.ScriptPath.Replace($Root, '').TrimStart('/\').Replace('\', '/')
    @{
        ruleId    = $_.RuleName
        level     = switch ($_.Severity) { 'Error' { 'error' } 'Warning' { 'warning' } default { 'note' } }
        message   = @{ text = $_.Message }
        locations = @(@{
            physicalLocation = @{
                artifactLocation = @{ uri = $relative; uriBaseId = '%SRCROOT%' }
                region           = @{ startLine = [int]$_.Line; startColumn = [int]$_.Column }
            }
        })
    }
})

@{
    '$schema' = 'https://json.schemastore.org/sarif-2.1.0.json'
    version   = '2.1.0'
    runs      = @(@{
        tool    = @{ driver = @{ name = 'PSScriptAnalyzer'; rules = @() } }
        results = $sarifResults
    })
} | ConvertTo-Json -Depth 20 | Set-Content $SarifPath -Encoding utf8
