param([string]$Url = "http://localhost:5088")
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri "$Url/health" -TimeoutSec 2
        if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 500) { break }
    } catch {}
    Start-Sleep -Milliseconds 800
}
$edge = "$env:ProgramFiles (x86)\Microsoft\Edge\Application\msedge.exe"
$chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
$brave = "$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe"
if (Test-Path $edge) { Start-Process $edge "--app=$Url --new-window"; exit }
if (Test-Path $chrome) { Start-Process $chrome "--app=$Url --new-window"; exit }
if (Test-Path $brave) { Start-Process $brave "--app=$Url --new-window"; exit }
Start-Process $Url
