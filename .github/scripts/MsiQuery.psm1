<#
.SYNOPSIS
    Reads identity fields out of an MSI package without installing it.

.DESCRIPTION
    The same COM interop was written three times - twice inline in workflow YAML and once in the lifecycle
    test - which is how the two copies drifted into asking slightly different questions about the same
    packages. It lives here once.

    Everything goes through WindowsInstaller.Installer late binding: the MSI object model has no usable
    primary interop assembly on a GitHub runner, so InvokeMember is the practical way in.
#>

Set-StrictMode -Version Latest

# Summary Information stream, PID_TEMPLATE. Holds "<platform>;<language>", e.g. "x64;1033" or "Intel;1033",
# and is the only authoritative statement of the architecture a package targets.
$script:TemplateSummaryProperty = 7

# Named Get-, not New-: it opens a read-only handle and changes nothing. A New- verb would (correctly) draw
# PSUseShouldProcessForStateChangingFunctions, since that verb promises a mutation this does not perform.
function Get-MsiDatabase {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    $installer = New-Object -ComObject WindowsInstaller.Installer
    return @{
        Installer = $installer
        Database  = $installer.GetType().InvokeMember(
            'OpenDatabase', 'InvokeMethod', $null, $installer, @($resolved, 0))
        Path      = $resolved
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

    $msi = Get-MsiDatabase -Path $Path
    $database = $msi.Database

    # Parameterised through a WHERE on a quoted literal: Name is caller-supplied, and string-building a
    # query around it is the SQL-injection shape even here. MSI SQL has no bound parameters for the SELECT
    # list, so the value is validated instead - property names are identifiers, nothing else.
    if ($Name -notmatch '^[A-Za-z_][A-Za-z0-9_.]*$') {
        throw "Invalid MSI property name '$Name'."
    }

    $view = $database.GetType().InvokeMember(
        'OpenView', 'InvokeMethod', $null, $database, @("SELECT Value FROM Property WHERE Property='$Name'"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if ($null -eq $record) { return $null }

    return $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
}

function Get-MsiPlatform {
    <#
    .SYNOPSIS
        The platform token from the summary Template - "x64" for a 64-bit package, "Intel" for 32-bit.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $summary = $installer.GetType().InvokeMember(
        'SummaryInformation', 'GetProperty', $null, $installer, @($resolved, 0))
    $template = $summary.GetType().InvokeMember(
        'Property', 'GetProperty', $null, $summary, @($script:TemplateSummaryProperty))

    # "x64;1033" -> "x64". An empty language suffix is legal, so split rather than assume.
    return ($template -split ';')[0]
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
    $database = $msi.Database

    # No caller input reaches this query - the whole table is read and filtered in PowerShell.
    $view = $database.GetType().InvokeMember(
        'OpenView', 'InvokeMethod', $null, $database,
        @('SELECT Dialog_, Control_, Event, Argument, Condition, Ordering FROM ControlEvent'))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null

    while ($true) {
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { break }

        $field = { param($i) $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, $i) }
        [pscustomobject]@{
            Dialog    = & $field 1
            Control   = & $field 2
            Event     = & $field 3
            Argument  = & $field 4
            Condition = & $field 5
            Ordering  = & $field 6
        }
    }
}

Export-ModuleMember -Function Get-MsiProperty, Get-MsiPlatform, Get-MsiControlEvent
