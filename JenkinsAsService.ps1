function Invoke-WriteLog {
    [CmdletBinding(ConfirmImpact = 'None',
        SupportsShouldProcess = $false)]
    param
    (
        [Parameter(Mandatory = $true)]
        [string]$LogString,

        [Parameter(Mandatory = $true)]
        [string]$LogType,

        [Parameter(Mandatory = $true)]
        [string]$LogPath
    )
	
    $UniqeProcessID = ([System.Diagnostics.Process]::GetCurrentProcess()).ID
    $DateTime = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    $Message = "$UniqeProcessID | $DateTime | $LogType | $LogString"
    Write-Output -InputObject $Message 
    "$Message" *>>"$LogPath"
    return $null
}

function Get-ScriptPath() {
    # If using PowerShell ISE
    if ($psISE) {
        $ScriptPath = Split-Path -Parent -Path $psISE.CurrentFile.FullPath
    }
    # If using PowerShell 3.0 or greater
    elseif ($PSVersionTable.PSVersion.Major -gt 3) {
        $ScriptPath = $PSScriptRoot
    }
    # If using PowerShell 2.0 or lower
    else {
        $ScriptPath = split-path -parent $MyInvocation.MyCommand.Path
    }

    # If still not found
    # I found this can happen if running an exe created using PS2EXE module
    if (-not $ScriptPath) {
        $ScriptPath = [System.AppDomain]::CurrentDomain.BaseDirectory.TrimEnd('\')
    }

    # Return result
    return $ScriptPath
}

Function Convert-XMLtoPSObject {
    Param (
        $XML
    )
    $Object = [System.Collections.ArrayList]@()
    $xml | ForEach-Object {
        $Name = $_.Name 
        $Value = ($_.property | where-object 'Name' -eq 'Value').'#text'
        $Deafult = ($_.property | where-object 'Name' -eq 'Deafult').'#text'
        if ($Deafult -eq 'Mandatory Field. No Deafult') {
            $Mandatory = $True
        }
        Else {
            $Mandatory = $False
        }
        $null = $Object.Add([PSCustomObject]@{
                'Name'      = $Name
                'Value'     = $Value
                'Mandatory' = $Mandatory
            })
    }
    return $Object
}

Function Set-DeafultXML {
    Param (
        [string]
        $Path,

        [string]
        $LogPath
    )
    [string]$DeafultXML = '<?xml version="1.0" encoding="utf-8"?>
    <Objects>
      <Object>
        <Property Name="JenkinsURL">
          <Property Name="Value"></Property>
          <Property Name="Description">Jenkine Full URL including Port.</Property>
          <Property Name="Example">https://jenkins.mydomain.com:443</Property>
          <Property Name="Deafult">Mandatory Field. No Deafult</Property>
        </Property>
        <Property Name="AgentName">
          <Property Name="Value"></Property>
          <Property Name="Description">Agent/Slave Name. Case-Sensative.</Property>
          <Property Name="Example">IISServer</Property>
          <Property Name="Deafult">Machine Host Name</Property>
        </Property>
        <Property Name="AgentSecret">
          <Property Name="Value"></Property>
          <Property Name="Description">Agent/Slave secret autogenerated by Jenkins. Case-Sensative.</Property>
          <Property Name="Example">12345aBcD</Property>
          <Property Name="Deafult">Mandatory Field. No Deafult</Property>
        </Property>
        <Property Name="JavaPath">
          <Property Name="Value"></Property>
          <Property Name="Description">Full path to the JDK/OpenJDK Bin Folder</Property>
          <Property Name="Example">C:\Program Files\Java\jdk-21.0.2\bin</Property>
          <Property Name="Deafult">If JAVA_HOME Environemnt Variable is set, use it.</Property>
        </Property>
      </Object>
    </Objects>'

    $Path = "$Path\JenkinsAsService.xml"
    
    $SaveResponse = Set-Content -Path $Path -Value $DeafultXML -Force -Encoding 'UTF8' -PassThru
    if ((($null, '') -contains $SaveResponse) -or ($SaveResponse -ne $DeafultXML)) {
        Invoke-WriteLog -LogPath "$LogPath" -LogString "Error Saving Deafult XML to Path: '$Path'" -LogType 'Error'
        exit
    }
}

Function Test-TCPConnection {
    Param($address, $port, [switch]$Quite , $timeout = 2000)
    $socket = New-Object System.Net.Sockets.TcpClient
    try {
        $result = $socket.BeginConnect($address, $port, $NULL, $NULL)
        if (!$result.AsyncWaitHandle.WaitOne($timeout, $False)) {
            if ($Quite -ne $true)
            { throw [System.Exception]::new('Connection Timeout') }
        }
        $socket.EndConnect($result) | Out-Null
        $Result = $socket.Connected
    }
    finally {
        $socket.Close()
        
    }
    if ($result -ne $true) { return $result.CompletedSynchronously }
    else { return $result }
    
}

##Check if Windows OS
if (([System.Environment]::OSVersion.Platform) -ne 'Win32NT' ) { exit }

$JenkinsPath = Get-ScriptPath
$XMLPath = "$JenkinsPath\JenkinsAsService.xml"
$AgentLog = "$JenkinsPath\agent.log"



#Read Settings File
if (Test-Path -Path $XMLPath -PathType 'Leaf') {
    $Content = (Get-Content -Path $XMLPath -Force)
    if (($null, '') -contains $Content) {
        Set-DeafultXML -Path $JenkinsPath  -LogPath $AgentLog
        Invoke-WriteLog -LogPath $AgentLog -LogString 'Please Fill in the XML Values and re-run the Service' -LogType 'Warning'
        exit    
    }
    else {
        [xml]$XML = $Content

        $Settings = Convert-XMLtoPSObject -XML ($XML.Objects.Object.property)

    }
}
else {
    Set-DeafultXML -Path $JenkinsPath
    Write-Warning -Message 'Please Fill in the XML Values and re-run the Service'
    exit
}



#Set Settings as Variables
foreach ($Setting in $Settings) {
    $Name = $Setting.Name
    $Value = $Setting.Value
    $Mandatory = $Setting.Mandatory
    if (($Setting.Mandatory -eq $true) -and (($null, '') -contains $Setting.Value)) {
        Invoke-WriteLog -LogPath $AgentLog -LogString  "Jenkins Failed to run: '$Name' is a mandatory field." -LogType 'Error'
        Exit
    }
    Set-Variable -Name $Setting.Name -Value $Setting.Value -Force
}

if (('', $null) -contains $AgentName) {
    $AgentName = [System.Net.Dns]::GetHostName()
    Invoke-WriteLog -LogPath $AgentLog -LogString  "'AgentName' is empty. Using '$AgentName' for this session" -LogType 'Information'
}

if ((('', $null) -contains $JavaPath) -or (-not (Test-Path -Path "$JavaPath\java.exe" -PathType 'Leaf'))) {
    Invoke-WriteLog -LogPath $AgentLog -LogString  "'JavaPath' is empty. Looking for JAVA_HOME Environment Variable" -LogType 'Information'
    if (Test-Path -Path (${env:JAVA_HOME} + '\java.exe') -PathType 'Leaf') {
        $JavaPath = ${env:JAVA_HOME}
        Invoke-WriteLog -LogPath $AgentLog -LogString  "JAVA_HOME Environment Variable Found. JavaPAth='$JavaPath' in this session" -LogType 'Information'

    }
    else {
        Invoke-WriteLog -LogPath $AgentLog -LogString  "'JavaPath' Value and 'JAVA_HOME' Environment Variable are empty or missing" -LogType 'Error'
        exit
    }
}
Else {
    if (-not (Test-Path -Path "$JavaPath\java.exe" -PathType Leaf)) {
        Invoke-WriteLog -LogPath $AgentLog -LogString  "Issue with 'JavaPath': The Path provided ($JavaPath) Does not exist or it does not contain a java.exe executable" -LogType 'Error'
        exit
    }
}

##Check Jenkins URL and Port communication
$Jenkins = $jenkinsURL.TrimStart('http://').TrimStart('https://')
$JenkinsDomain = $Jenkins.Substring(0, ($Jenkins.IndexOf(":")))
$JenkinsPort = $Jenkins.Substring(( $Jenkins.IndexOf(":") + 1 ))

if (-not (Test-TCPConnection -address $JenkinsDomain -port $JenkinsPort -Quite)) {
    Invoke-WriteLog -LogPath $AgentLog -LogString  "Issue with 'jenkinsURL': Unable to communicate with '$JenkinsDomain' over Port '$JenkinsPort'" -LogType 'Error'
    exit
}


##Cleaning Junk Variables From Memory
Remove-Variable -Name @('Content', '$Settings', 'XML', 'XMLPath', 'Jenkins', 'JenkinsDomain', 'JenkinsPort') -Force -ErrorAction 'SilentlyContinue'


#Set TLS Security. TLS 1.2 and above. Higher to Lower TLS.
[enum]::GetValues('Net.SecurityProtocolType') | Where-Object -FilterScript { $_ -ge 'Tls12' } | Sort-Object -Descending -Unique | ForEach-Object {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor $_
}


#Reciving Agent.Jar from Jenkins
Invoke-WriteLog -LogPath $AgentLog -LogString 'Attepting to establish connection with Jenkins' -LogType 'Information'
Invoke-WebRequest -Uri "$JenkinsURL/jnlpJars/agent.jar" -OutFile "$JenkinsPath\agent.jar" -UseBasicParsing -Method 'Get' *>>"$AgentLog"

#Running The Agent
Invoke-WriteLog -LogPath $AgentLog -LogString 'Attepting to start Jenkins Agent' -LogType 'Information'
& "$JavaPath\java.exe" -jar "$JenkinsPath\agent.jar" -url "$JenkinsURL/" -secret "$AgentSecret" -name "$AgentName" -workDir "$JenkinsPath" *>>"$AgentLog"