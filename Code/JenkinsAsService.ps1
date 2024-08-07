
function Convert-FunctionToString {
    param (
        [Parameter(Mandatory = $True)]
        [string[]]$FunctionToConvert
    )
    $AllFunctions = foreach ($FunctionName in $FunctionToConvert) {
		
        $Function = Get-Command -Name $FunctionName -CommandType Function -ErrorAction Stop
        $ScriptBlock = $Function.ScriptBlock
        if ($null -ne $ScriptBlock) {
            [string]::Format("`r`nfunction {0} {{{1}}}", $FunctionName, $ScriptBlock)
        }
        else {
            Invoke-WriteLog -LogString "Function $FunctionName does not have a Script Block and cannot be converted." -LogType 2
        }
    }
    return ($AllFunctions -join "`r`n")
}

function Invoke-WriteLog {
    param
    (
        [Parameter(Mandatory = $false)]
        [string]$LogString = $null,
		
        [Parameter(Mandatory = $false)]
        [ValidateSet('0', '1', '2', '3')]
        [int]$LogType = 0,
		
        [Parameter(Mandatory = $false)]
        [string]$LogPath = $global:AgentLog,
		
        [Parameter(Mandatory = $false)]
        [int]$ProcessID = $Global:UniqeProcessID,
		
		
        [Parameter(Mandatory = $false)]
        [int]$DebugMode = $global:DebugMode
    )
	
    enum LogType {
        Information = 0
        Warning = 1
        Error = 2
        Debug = 3
    }
	
	
    $ELogType = ([LogType]$LogType)
	
    if (($DebugMode -eq 0) -and ($LogType -eq 3)) {
        ## If Debug message while debug mode is false, skip
        return $null
    }
	
    if ([string]::IsNullOrWhiteSpace($LogString)) {
        ## If empty message, skip.
        return $null
    } 
	

    $DateTime = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    $Message = "$UniqeProcessID | $DateTime | $ELogType | $LogString"
    Add-Content -Path "$LogPath" -Value $Message -Force
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
        $ScriptPath = split-path -parent $HostInvocation.MyCommand.Path
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
        $Default = ($_.property | where-object 'Name' -eq 'Default').'#text'
        if ($Default -eq 'Mandatory Field. No Default') {
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
Function Test-TCPConnection {
    Param ([string]$address,
        $port,
        [switch]$Quite,
        [int]$timeout = 2000)
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
Function Invoke-DefaultXML {
    Param (
        [string]$Path,
        [string]$LogPath
    )
    [string]$DefaultXML = '<?xml version="1.0" encoding="utf-8"?>
<Objects>
  <Object>
    <Property Name="JenkinsURL">
      <Property Name="Value"></Property>
      <Property Name="Description">Jenkine Full URL including Port.</Property>
      <Property Name="Example">https://jenkins.mydomain.com:443</Property>
      <Property Name="Default">Mandatory Field. No Default</Property>
      <Property Name="Type">String</Property>
    </Property>
    <Property Name="AgentName">
      <Property Name="Value"></Property>
      <Property Name="Description">Agent/Slave Name. Case-Sensative.</Property>
      <Property Name="Example">IISServer</Property>
      <Property Name="Default">Machine Host Name</Property>
      <Property Name="Type">String</Property>
    </Property>
    <Property Name="AgentSecret">
      <Property Name="Value"></Property>
      <Property Name="Description">Agent/Slave secret autogenerated by Jenkins. Case-Sensative.</Property>
      <Property Name="Example">12345aBcD</Property>
      <Property Name="Default">Mandatory Field. No Default</Property>
      <Property Name="Type">String</Property>
    </Property>
    <Property Name="JavaPath">
      <Property Name="Value"></Property>
      <Property Name="Description">Full path to the JDK/OpenJDK Bin Folder</Property>
      <Property Name="Example">C:\Program Files\Java\jdk-21.0.2\bin</Property>
      <Property Name="Default">If JAVA_HOME Environemnt Variable is set, use it.</Property>
      <Property Name="Type">String</Property>
    </Property>
    <Property Name="CustomArguments">
      <Property Name="Value"></Property>
      <Property Name="Description">Additional arguments to pass to java.exe</Property>
      <Property Name="Example">-noCertificateCheck</Property>
      <Property Name="Default"></Property>
      <Property Name="Type">String</Property>
    </Property>
    <Property Name="DebugMode">
      <Property Name="Value"></Property>
      <Property Name="Description">1= Enable Debug Logs. 0= Disable Debug Logs</Property>
      <Property Name="Example">0</Property>
      <Property Name="Default">0</Property>
      <Property Name="Type">Boolean</Property>
    </Property>
  </Object>
</Objects>'
	
    $Path = "$Path\JenkinsAsService.xml"
	
    $SaveResponse = Set-Content -Path $Path -Value $DefaultXML -Force -Encoding 'UTF8' -PassThru
    if ((($null, '') -contains $SaveResponse) -or ($SaveResponse -ne $DefaultXML)) {
        Invoke-WriteLog -LogPath "$LogPath" -LogString "Error Saving Default XML to Path: '$Path'" -LogType 2
        Stop-MyService
        exit
    }
}

function Stop-MyService {
    $global:bRunService = $false
    $null = Get-Job | Stop-Job | Remove-Job
    $global:bServiceRunning = $false
    Invoke-WriteLog -LogString 'Jenkins Agent Stopped!' -LogType 1
    return
}

function Start-MyService {
    TRY {
        ##Setting Defult Variables
        $Global:JenkinsPath = Get-ScriptPath
        $XMLPath = "$JenkinsPath\JenkinsAsService.xml"
        $Global:AgentLog = "$JenkinsPath\agent.log"
		
        Set-Content -Path $AgentLog -Value $null -Force -NoNewline
		
		
		
        #Read Settings File
        if (Test-Path -Path $XMLPath -PathType 'Leaf') {
            $Content = (Get-Content -Path $XMLPath -Force)
            if ([string]::IsNullOrWhiteSpace($Content)) {
                Invoke-DefaultXML -Path $JenkinsPath -LogPath $AgentLog
                Invoke-WriteLog -LogString "Setting file Created: '$XMLPath'. Fill the Values and re-run the Service" -LogType 2
                $global:bServiceRunning = $false
                Stop-MyService
                exit
            }
            else {
                [xml]$XML = $Content
				
                $Settings = Convert-XMLtoPSObject -XML ($XML.Objects.Object.property)
				
            }
        }
        else {
            Invoke-DefaultXML -Path $JenkinsPath
            Invoke-WriteLog -LogString "Setting file Created: '$XMLPath'. Fill the Values and re-run the Service" -LogType 2
            Stop-MyService
            exit
        }
		
		
		
        #Set Settings as Variables
        foreach ($Setting in $Settings) {
            $Name = $Setting.Name
            $Value = $Setting.Value
            $Mandatory = $Setting.Mandatory
            if (($Setting.Mandatory -eq $true) -and (($null, '') -contains $Setting.Value)) {
                Invoke-WriteLog -LogString "Jenkins Failed to run: '$Name' is a mandatory field." -LogType 2
                Stop-MyService
                exit
            }
            Set-Variable -Name $Setting.Name -Value $Setting.Value -Force -Scope 'Global'
        }
		
        $Global:UniqeProcessID = ([System.Diagnostics.Process]::GetCurrentProcess()).ID
		
        $global:bRunService = $true
        $global:bServiceRunning = $false
        $global:bServicePaused = $false
		
    }
    Catch {
        ## Write Error log
        Invoke-WriteLog -LogPath "$AgentLog" -LogString ($_ | Select-Object -Property '*' | Out-String) -LogType 2
        Stop-MyService
        exit
    }
}



function Invoke-MyService {
    Try {
        $global:bServiceRunning = $true
        ##Checking Debug Mode
		
        if (([string]::IsNullOrWhiteSpace($Global:DebugMode)) -or ($Global:DebugMode -eq 0) -or ($Global:DebugMode -eq $false)) {
            $Global:DebugMode = 0
        }
        elseif (($Global:DebugMode -eq 1) -or ($Global:DebugMode -eq $true)) {
            $Global:DebugMode = 1
        }
        else {
            $Global:DebugMode = 0
        }
		
        ##Checking Agent Name
        if ([string]::IsNullOrWhiteSpace($AgentName)) {
            $AgentName = [System.Net.Dns]::GetHostName()
            Invoke-WriteLog -LogString "'AgentName' is empty. Using '$AgentName' for this session" -LogType 3
        }
		
        ##Checking Java Path
        if (([string]::IsNullOrWhiteSpace($JavaPath)) -or (-not (Test-Path -Path "$JavaPath\java.exe" -PathType 'Leaf'))) {
            Invoke-WriteLog -LogString "'JavaPath' is empty. Looking for JAVA_HOME Environment Variable" -LogType 3
            if (Test-Path -Path (${env:JAVA_HOME} + '\java.exe') -PathType 'Leaf') {
                $JavaPath = ${env:JAVA_HOME}
                Invoke-WriteLog -LogString "JAVA_HOME Environment Variable Found. JavaPAth='$JavaPath' in this session" -LogType 3
				
            }
            else {
                Invoke-WriteLog -LogString "'JavaPath' Value and 'JAVA_HOME' Environment Variable are empty or missing" -LogType 2
                Stop-MyService
                exit
				
				
            }
        }
        Else {
            if (-not (Test-Path -Path "$JavaPath\java.exe" -PathType Leaf)) {
                Invoke-WriteLog -LogString "Issue with 'JavaPath': The Path provided ($JavaPath) Does not exist or it does not contain a java.exe executable" -LogType 1
                Stop-MyService
                exit
				
            }
        }
		
        ##Check Jenkins URL and Port communication
        $Jenkins = $jenkinsURL.TrimStart('http://').TrimStart('https://')
        $JenkinsDomain = $Jenkins.Substring(0, ($Jenkins.IndexOf(":")))
        $JenkinsPort = $Jenkins.Substring(($Jenkins.IndexOf(":") + 1))
		
        if ($JenkinsPort -notmatch "^\d+$") {
            Invoke-WriteLog -LogString "Issue with 'JenkinsPort': Port provided ($JenkinsPort) is not an Integer Value" -LogType 1
            Stop-MyService
            exit
        }
		
        if ((Test-TCPConnection -address $JenkinsDomain -port $JenkinsPort -Quite) -ne $true) {
            Invoke-WriteLog -LogString "Issue with 'jenkinsURL': Unable to communicate with '$JenkinsDomain' over Port '$JenkinsPort'" -LogType 1
            Stop-MyService
            exit
        }
		
		
        ##Cleaning Junk Variables From Memory
        Remove-Variable -Name @('Content', 'Settings', 'XML', 'XMLPath', 'Jenkins', 'JenkinsDomain', 'JenkinsPort') -Force -ErrorAction 'SilentlyContinue'
		
		
        #Set TLS Security. TLS 1.2 and above. Higher to Lower TLS.
        [enum]::GetValues('Net.SecurityProtocolType') | Where-Object -FilterScript { $_ -ge 'Tls12' } | Sort-Object -Descending -Unique | ForEach-Object {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor $_
        }
		
		
        #Reciving Agent.Jar from Jenkins
        Invoke-WriteLog -LogString 'Attepting to establish connection with Jenkins'
        $null = Invoke-WebRequest -Uri "$JenkinsURL/jnlpJars/agent.jar" -OutFile "$JenkinsPath\agent.jar" -UseBasicParsing -Method 'Get' -PassThru -ErrorAction 'Stop'
        Invoke-WriteLog -LogString 'Conection with Jenkins establish'
		
		
        $Global:Run = "`"$JavaPath\java.exe`" -jar `"$JenkinsPath\agent.jar`" -url `"$JenkinsURL/`" -secret `"$AgentSecret`" -name `"$AgentName`" -workDir `"$JenkinsPath`" `"$CustomArguments`""
        [string]$Global:InvokeWriteLog = (Convert-FunctionToString -FunctionToConvert 'Invoke-WriteLog')
		
        if ($global:bRunService -eq $true) {
            $null = Start-job -Name 'Jenkins' -ArgumentList ($InvokeWriteLog, $AgentLog, $Run, $DebugMode, $UniqeProcessID) -ScriptBlock {
                param ($InvokeWriteLog,
                    $AgentLog,
                    $Run,
                    $DebugMode,
                    $UniqeProcessID)
                Invoke-Expression -Command "$InvokeWriteLog"
                Invoke-WriteLog -LogPath $AgentLog -LogString 'Attepting to start Jenkins Agent'
                Invoke-Expression -Command "& $Run *>&1 | ForEach-Object { 
                    `$Output = `$_.tostring()

                    if (`$Output -match '^INFO:.*') {
                        Invoke-WriteLog -LogPath `"$AgentLog`" -LogString (`$Output.TrimStart(' INFO: '))  -DebugMode `$DebugMode -ProcessID `$UniqeProcessID
                    }

                    elseif ([bool]((`$Output.Substring(0, 6)) -as [datetime])) {
                        `$Length=(((`$Output -split 'AM')[0] -split 'PM')[0]).Length
                        `$Output=`$Output.Substring(`$Length+3)
                        Invoke-WriteLog -LogPath `"$AgentLog`" -LogString `$Output -LogType 3 -DebugMode `$DebugMode -ProcessID `$UniqeProcessID
                    }
        
                    else {
                        Invoke-WriteLog -LogPath `"$AgentLog`" -LogString `$Output -DebugMode `$DebugMode -ProcessID `$UniqeProcessID
                    }
                }"
                $global:bRunService = $false
            }
        }
        While ($global:bRunService -eq $true) { }
        $global:bServiceRunning = $false
    }
    Catch {
        ## Write Error log
        Invoke-WriteLog -LogPath "$AgentLog" -LogString ($_ | Select-Object -Property '*' | Out-String) -LogType 2
        Stop-MyService
        exit
    }
	
}

function Pause-MyService {
    # Service is being paused
    # Save state 
    $global:bServicePaused = $true
    # Note that the thread your PowerShell script is running on is not suspended on 'pause'.
    # It is your responsibility in the service loop to pause processing until a 'continue' command is issued.
    # It is recommended to sleep for longer periods between loop iterations when the service is paused.
    # in order to prevent excessive CPU usage by simply waiting and looping.
}

function Continue-MyService {
    # Service is being continued from a paused state
    # Restore any saved states if needed
    $global:bServicePaused = $false
}
