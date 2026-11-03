param([Parameter(Mandatory = $true)][string]$DotnetPath)
$ErrorActionPreference = 'Stop'
$serverRoot = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $serverRoot 'bin/Debug/net8.0/BetterRustPlus.Server.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Build the server in Debug before running the smoke test.' }
$scratch = Join-Path $PSScriptRoot '.smoke'
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$checks = 0
function Assert-Check($condition, $message) {
    if (-not $condition) { throw $message }
    $script:checks++
}
foreach ($configured in @($false, $true)) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $address = "http://127.0.0.1:$port"
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Resolve-Path -LiteralPath $DotnetPath).Path
    $start.WorkingDirectory = $serverRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.ArgumentList.Add($dll)
    $start.ArgumentList.Add('--urls')
    $start.ArgumentList.Add($address)
    $key = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $start.Environment.Remove('BETTERRUSTPLUS_INGEST_KEY') | Out-Null
    if ($configured) { $start.Environment['BETTERRUSTPLUS_INGEST_KEY'] = $key }
    $start.Environment['Alerts__DryRun'] = 'true'
    $start.Environment['RustSource__Enabled'] = 'false'
    $start.Environment['DiscordBot__Enabled'] = 'false'
    $start.Environment['Alerts__AllowedServerIds__0'] = 'smoke-server'
    $start.Environment['Alerts__Rules__0__Id'] = 'smoke-rule'
    $start.Environment['Alerts__Rules__0__Kinds__0'] = 'raid'
    $start.Environment['Alerts__Rules__0__CooldownSeconds'] = '0'
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            if ($process.HasExited) { throw 'Smoke-test host exited before becoming ready.' }
            try { $health = Invoke-RestMethod "$address/health" -TimeoutSec 1; $ready = $true; break } catch { Start-Sleep -Milliseconds 200 }
        }
        Assert-Check $ready 'Health endpoint did not become ready.'
        Assert-Check ($health.processId -eq $process.Id) 'Health must identify the temporary host, not another process on the port.'
        Assert-Check ($health.gameSourceConnected -eq $false) 'Health falsely reports a game connection.'
        Assert-Check ($health.mode -eq 'dry-run') 'Smoke test must never enable live Discord delivery.'
        $event = @{ id = [Guid]::NewGuid().ToString(); serverId = 'smoke-server'; kind = 'raid'; occurredAt = [DateTimeOffset]::UtcNow.ToString('o'); evidence = 'confirmed' }
        $body = $event | ConvertTo-Json
        $anonymous = Invoke-WebRequest "$address/events" -Method Post -ContentType 'application/json' -Body $body -SkipHttpErrorCheck
        $anonymousTeam = Invoke-WebRequest "$address/team" -SkipHttpErrorCheck
        if (-not $configured) {
            Assert-Check ($anonymous.StatusCode -eq 503) 'Unconfigured ingestion must fail closed.'
            Assert-Check ($anonymousTeam.StatusCode -eq 503) 'Unconfigured team endpoint must fail closed.'
            continue
        }
        Assert-Check ($anonymous.StatusCode -eq 401) 'Missing authentication was accepted.'
        Assert-Check ($anonymousTeam.StatusCode -eq 401) 'Team positions were exposed without authentication.'
        $headers = @{ Authorization = "Bearer $key" }
        $team = Invoke-RestMethod "$address/team" -Headers $headers
        Assert-Check ($team.available -eq $false -and $team.members.Count -eq 0) 'Disabled source returned live or invented positions.'
        $wrongKey = Invoke-WebRequest "$address/events" -Method Post -ContentType 'application/json' -Body $body -Headers @{ Authorization = 'Bearer incorrect' } -SkipHttpErrorCheck
        Assert-Check ($wrongKey.StatusCode -eq 401) 'Incorrect key was accepted.'
        $result = Invoke-RestMethod "$address/events" -Method Post -ContentType 'application/json' -Body $body -Headers $headers
        Assert-Check ($result.simulated -eq 1 -and $result.delivered -eq 0) 'Dry-run was not simulated.'
        $duplicate = Invoke-RestMethod "$address/events" -Method Post -ContentType 'application/json' -Body $body -Headers $headers
        Assert-Check ($duplicate.duplicate -eq 1) 'HTTP duplicate was not suppressed.'
        $event.id = [Guid]::NewGuid().ToString()
        $event.serverId = 'not-allowed'
        $rejected = Invoke-WebRequest "$address/events" -Method Post -ContentType 'application/json' -Body ($event | ConvertTo-Json) -Headers $headers -SkipHttpErrorCheck
        Assert-Check ($rejected.StatusCode -eq 400) 'Unconfigured server was accepted over HTTP.'
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
Write-Output "Passed $checks HTTP smoke checks. Both temporary hosts stopped; no Discord requests were made."
