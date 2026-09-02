<#
.SYNOPSIS
    Reads identity fields out of an MSI package without installing it.

.DESCRIPTION
    The same COM interop was written three times - twice inline in workflow YAML and once in the lifecycle
    test - which is how the two copies drifted into asking slightly different questions about the same
    packages. It lives here once.

    Everything goes through WindowsInstaller.Installer late binding: the MSI object model has no usable
    primary interop assembly on a GitHub runner, so InvokeMember is the practical way in. That interop is
    itself written once, in Invoke-MsiQuery - the public functions below are a query string plus a projection.
#>

Set-StrictMode -Version Latest

# Summary Information stream, PID_TEMPLATE. Holds "<platform>;<language>" - "x64;1033", "Intel;1033" or
# "Arm64;1033" - and is the only authoritative statement of the architecture a package targets.
$script:TemplateSummaryProperty = 7

# The pieces of Windows Installer trivia nobody remembers: a 32-bit package's platform token is spelled
# "Intel", not "x86", and the ARM64 token is "Arm64" in that exact casing while the x64 one is lower-case.
# Held here, next to the function that reads the field, so no caller has to know any of it - and matched
# case-insensitively below, since the comparison is against an emitted constant rather than user input.
$script:PlatformTokens = [ordered]@{
    x64   = 'x64'
    x86   = 'Intel'
    arm64 = 'Arm64'
}

# Named Get-, not New-: it opens a read-only handle and changes nothing. A New- verb would (correctly) draw
# PSUseShouldProcessForStateChangingFunctions, since that verb promises a mutation this does not perform.
# Every caller must pass the result to Close-MsiDatabase, or the package file stays open behind a live COM
# reference until GC - which matters here because callers hand the same file to msiexec straight afterwards.
function Get-MsiDatabase {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    $installer = New-Object -ComObject WindowsInstaller.Installer
    return @{
        Installer = $installer
        Database  = $installer.GetType().InvokeMember(
            'OpenDatabase', 'InvokeMethod', $null, $installer, @($resolved, 0))
    }
}

function Close-MsiDatabase {
    param([Parameter(Mandatory)][hashtable]$Msi)
    foreach ($key in @('Database', 'Installer')) {
        if ($Msi.ContainsKey($key) -and $null -ne $Msi[$key]) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Msi[$key])
        }
    }
}

function Invoke-MsiQuery {
    <#
    .SYNOPSIS
        Runs an MSI SQL query and returns each row as an object[] of its first $FieldCount string fields.
    .DESCRIPTION
        The whole late-bound OpenView/Execute/Fetch/StringData dance, in the only place it is written.
        The view is closed and every record released as it goes, so a query does not leave the package open.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]$Database,
        [Parameter(Mandatory)][string]$Sql,
        [Parameter(Mandatory)][int]$FieldCount
    )

    $view = $Database.GetType().InvokeMember(
        'OpenView', 'InvokeMethod', $null, $Database, @($Sql))
    try {
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null

        while ($true) {
            $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if ($null -eq $record) { break }

            try {
                # A plain loop rather than a per-row scriptblock: the fields are read by index and nothing
                # about the projection changes between rows.
                $row = @()
                foreach ($i in 1..$FieldCount) {
                    $row += $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, $i)
                }
                # -NoEnumerate so each row reaches the caller as one object[] rather than being flattened
                # into a single stream of fields.
                Write-Output -NoEnumerate -InputObject $row
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
    }
    finally {
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}

function Get-MsiProperty {
    <#
    .SYNOPSIS
        A single row from the package's Property table, or $null when the property is absent.
    .EXAMPLE
        Get-MsiProperty -Path .\package.msi -Name ProductVersion
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    # Parameterised through a WHERE on a quoted literal: Name is caller-supplied, and string-building a
    # query around it is the SQL-injection shape even here. MSI SQL has no bound parameters for the SELECT
    # list, so the value is validated instead - property names are identifiers, nothing else.
    if ($Name -notmatch '^[A-Za-z_][A-Za-z0-9_.]*$') {
        throw "Invalid MSI property name '$Name'."
    }

    $msi = Get-MsiDatabase -Path $Path
    try {
        $rows = @(Invoke-MsiQuery -Database $msi.Database -FieldCount 1 `
                -Sql "SELECT Value FROM Property WHERE Property='$Name'")
        if ($rows.Count -eq 0) { return $null }
        return $rows[0][0]
    }
    finally { Close-MsiDatabase -Msi $msi }
}

function Get-MsiPlatform {
    <#
    .SYNOPSIS
        The platform token from the summary Template - "x64", "Intel" for 32-bit, or "Arm64".
    .DESCRIPTION
        Internal: the raw token is the one thing this module exists to stop callers handling. Everything
        outside compares architectures, which is what Get-MsiArchitecture returns.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $summary = $installer.GetType().InvokeMember(
            'SummaryInformation', 'GetProperty', $null, $installer, @($resolved, 0))
        try {
            $template = $summary.GetType().InvokeMember(
                'Property', 'GetProperty', $null, $summary, @($script:TemplateSummaryProperty))
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }

    # "x64;1033" -> "x64". An empty language suffix is legal, so split rather than assume.
    return ($template -split ';')[0]
}

function Get-MsiArchitecture {
    <#
    .SYNOPSIS
        The architecture a package targets, normalised to x64/x86/arm64 - or the raw token if it is none.
    .DESCRIPTION
        What callers actually want to compare against an x64/x86/arm64 parameter, without each of them having
        to know how a 32-bit package spells itself.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    $token = Get-MsiPlatform -Path $Path
    foreach ($platform in $script:PlatformTokens.Keys) {
        if ($script:PlatformTokens[$platform] -eq $token) { return $platform }
    }
    return $token
}

function Get-MsiControlEvent {
    <#
    .SYNOPSIS
        Every row of the package's ControlEvent table - the wizard's navigation graph.
    .DESCRIPTION
        The UI sequence is authored, never executed by the /quiet lifecycle test, so the only automated way to
        check that a dialog route is correctly gated is to read the routes back out of the built package. This
        also catches the fragment being dropped by the linker altogether, which has happened here before: a
        missing UIRef silently shipped an MSI with none of the custom pages in it.
    .OUTPUTS
        Objects with Dialog, Control, Event, Argument, Condition and Ordering.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][string]$Path)

    $msi = Get-MsiDatabase -Path $Path
    try {
        # No caller input reaches this query - the whole table is read and filtered by the caller.
        Invoke-MsiQuery -Database $msi.Database -FieldCount 6 `
            -Sql 'SELECT Dialog_, Control_, Event, Argument, Condition, Ordering FROM ControlEvent' |
            ForEach-Object {
                [pscustomobject]@{
                    Dialog    = $_[0]
                    Control   = $_[1]
                    Event     = $_[2]
                    Argument  = $_[3]
                    Condition = $_[4]
                    Ordering  = $_[5]
                }
            }
    }
    finally { Close-MsiDatabase -Msi $msi }
}

function Get-MsiExecuteSequence {
    <#
    .SYNOPSIS
        The package's InstallExecuteSequence as Action/Sequence pairs.
    .DESCRIPTION
        Custom-action ORDER carries real constraints that nothing in the authoring enforces and no build
        catches - a CA scheduled a few hundred sequence numbers too early still links, still installs on a
        machine where the accident is harmless, and fails only against a real install. Reading the table
        back turns that into a static check. Sequence is an integer column, so it is read with IntegerData;
        StringData returns the formatted text and would sort lexically.
    .OUTPUTS
        Objects with Action and Sequence, ascending.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][string]$Path)

    $msi = Get-MsiDatabase -Path $Path
    try {
        $view = $msi.Database.GetType().InvokeMember(
            'OpenView', 'InvokeMethod', $null, $msi.Database,
            @('SELECT Action, Sequence FROM InstallExecuteSequence'))
        try {
            $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
            while ($true) {
                $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
                if ($null -eq $record) { break }
                try {
                    [pscustomobject]@{
                        Action   = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
                        Sequence = [int]$record.GetType().InvokeMember('IntegerData', 'GetProperty', $null, $record, 2)
                    }
                }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            }
        }
        finally {
            $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
    }
    finally { Close-MsiDatabase -Msi $msi }
}

function Get-MsiCustomAction {
    <#
    .SYNOPSIS
        The package's CustomAction table as Action/Type/Target triples.
    .DESCRIPTION
        Read back so the authored command lines can be checked, since the MSI's own validation has nothing to
        say about their CONTENT - ICE03 caps Target at 255 characters and stops there.
    .OUTPUTS
        Objects with Action, Type and Target.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][string]$Path)

    $msi = Get-MsiDatabase -Path $Path
    try {
        # No caller input reaches this query - the whole table is read and filtered by the caller.
        Invoke-MsiQuery -Database $msi.Database -FieldCount 3 `
            -Sql 'SELECT Action, Type, Target FROM CustomAction' |
            ForEach-Object {
                [pscustomobject]@{ Action = $_[0]; Type = $_[1]; Target = $_[2] }
            }
    }
    finally { Close-MsiDatabase -Msi $msi }
}

Export-ModuleMember -Function Get-MsiProperty, Get-MsiArchitecture, Get-MsiControlEvent, `
    Get-MsiExecuteSequence, Get-MsiCustomAction
