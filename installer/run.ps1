# BootstrapMate Simple Runner Script
# This script provides a convenient way to run the BootstrapMate installer
# with the configured bootstrap URL for this organization
# Generated during build with URL: https://cimian.example.com/bootstrap/management.json

Write-Host "Running BootstrapMate with configured URL..."
& 'C:\Program Files\BootstrapMate\installapplications.exe' --url https://cimian.example.com/bootstrap/management.json --no-dialog
