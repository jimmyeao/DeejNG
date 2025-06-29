# PowerShell script to clear DeejNG settings
$settingsPath = "$env:LOCALAPPDATA\DeejNG\settings.json"

if (Test-Path $settingsPath) {
    Remove-Item $settingsPath -Force
    Write-Host "Settings cleared successfully!" -ForegroundColor Green
    Write-Host "Please restart DeejNG to regenerate settings from your hardware." -ForegroundColor Yellow
} else {
    Write-Host "No settings file found at: $settingsPath" -ForegroundColor Yellow
}

Read-Host "Press Enter to exit"
