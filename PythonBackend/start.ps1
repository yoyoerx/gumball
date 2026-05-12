# start.ps1 — Launch server.py then diagnostic.py
# Usage: .\start.ps1 [-ServerHost <ip>]
#   ServerHost defaults to config.SERVER_HOST if not specified.

param(
    [string]$ServerHost = ""
)

if (-not $ServerHost) {
    try { $ServerHost = (python -c "import config; print(config.SERVER_HOST)").Trim() } catch {}
    if (-not $ServerHost -or $ServerHost -eq "YOUR_SERVER_IP") {
        Write-Host "ERROR: Set SERVER_HOST in config.py or pass -ServerHost <ip>" -ForegroundColor Red
        exit 1
    }
}

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dir

# Kill any stale server.py instances that would block port binding
$stale = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like "*server.py*" }
if ($stale) {
    Write-Host "[0/3] Stopping $($stale.Count) stale server.py process(es) ..." -ForegroundColor Yellow
    $stale | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}

Write-Host "[1/3] Starting server.py ..."
$server = Start-Process python -ArgumentList "server.py" `
    -WorkingDirectory $dir -PassThru -NoNewWindow:$false

if (-not $server) {
    Write-Host "ERROR: Failed to start server.py" -ForegroundColor Red
    exit 1
}
Write-Host "      server PID $($server.Id)"

# Give the server time to:
#   - load the YOLO model        (~2-3s cold, faster warm)
#   - bind the WebSocket ports   (~0.1s)
#   - start the motor-init thread (async, diagnostic tolerates not-ready)
$wait = 6
Write-Host "[2/3] Waiting ${wait}s for server to initialise ..."
for ($i = $wait; $i -gt 0; $i--) {
    Write-Host "      $i..." -NoNewline
    Start-Sleep -Seconds 1
}
Write-Host ""

Write-Host "[3/3] Starting diagnostic.py (host=$ServerHost) ..."
python diagnostic.py $ServerHost

# When diagnostic exits, offer to kill the server
Write-Host ""
$ans = Read-Host "Diagnostic closed. Stop server (PID $($server.Id))? [Y/n]"
if ($ans -eq "" -or $ans -match "^[Yy]") {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    Write-Host "Server stopped."
} else {
    Write-Host "Server left running (PID $($server.Id))."
}
