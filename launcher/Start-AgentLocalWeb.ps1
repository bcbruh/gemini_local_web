$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
$appDirectory = Join-Path $repositoryRoot 'src\AgentLocalWeb.AppHost'
$appDll = Join-Path $appDirectory 'bin\Release\net10.0-windows\AgentLocalWeb.AppHost.dll'
$chromeCandidates = @(
    'C:\Program Files\Google\Chrome\Application\chrome.exe',
    'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe',
    (Join-Path $env:LOCALAPPDATA 'Google\Chrome\Application\chrome.exe')
)
$chromePath = $chromeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

function Open-AgentChrome {
    param([Parameter(Mandatory = $true)][string]$Url)

    if ($chromePath) {
        Start-Process -FilePath $chromePath -ArgumentList $Url
    }
    else {
        Start-Process $Url
    }
}

function Find-RunningAgentUrl {
    $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -in @('dotnet.exe', 'AgentLocalWeb.AppHost.exe') -and
            $_.CommandLine -match 'AgentLocalWeb\.AppHost'
        }

    foreach ($process in $processes) {
        $listeners = Get-NetTCPConnection -State Listen -OwningProcess $process.ProcessId `
            -ErrorAction SilentlyContinue | Where-Object { $_.LocalAddress -eq '127.0.0.1' }
        foreach ($listener in $listeners) {
            $url = "http://127.0.0.1:$($listener.LocalPort)"
            try {
                $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 1
                if ($response.StatusCode -eq 200) {
                    return $url
                }
            }
            catch {
                # Ignore stale processes and unrelated listeners.
            }
        }
    }

    return $null
}

$runningUrl = Find-RunningAgentUrl
if ($runningUrl) {
    Open-AgentChrome -Url $runningUrl
    exit 0
}

if (-not (Test-Path -LiteralPath $dotnetPath)) {
    throw "Local .NET runtime not found: $dotnetPath"
}
if (-not (Test-Path -LiteralPath $appDll)) {
    throw "Release build not found: $appDll"
}

$logDirectory = Join-Path $env:LOCALAPPDATA 'AgentLocalWeb\Logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$standardLog = Join-Path $logDirectory 'desktop-launcher.log'
$errorLog = Join-Path $logDirectory 'desktop-launcher.error.log'

Start-Process `
    -FilePath $dotnetPath `
    -ArgumentList @($appDll, '--brain=gemini', '--browser=chrome') `
    -WorkingDirectory $appDirectory `
    -WindowStyle Hidden `
    -RedirectStandardOutput $standardLog `
    -RedirectStandardError $errorLog
