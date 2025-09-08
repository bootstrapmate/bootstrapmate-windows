# Git History Cleanup Script
# This script removes sensitive information from Git history

param(
    [switch]$Execute,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Write-Host "=== Git History Cleanup for BootstrapMate ===" -ForegroundColor Magenta
Write-Host ""

# Define sensitive patterns to remove
$sensitivePatterns = @{
    "ExampleOrgU Intune Windows Enterprise Certificate" = "ENTERPRISE_CERT_PLACEHOLDER"
    "https://cimian.example.com/bootstrap/management.json" = "https://your-domain.com/bootstrap/management.json"
    "cimian.example.com" = "your-domain.com"
    "ExampleOrgU" = "YourOrg"
    "ecuad.ca" = "your-domain.com"
    "1423F241DFF85AD2C8F31DBD70FB597DAC85BA4B" = "YOUR_CERTIFICATE_THUMBPRINT_HERE"
    "RODCHRISTIANSEN" = "EXAMPLE-USER"
    "ca.emilycarru.winadmins" = "com.yourorg.packages"
}

Write-Host "Sensitive patterns to be cleaned:" -ForegroundColor Yellow
foreach ($pattern in $sensitivePatterns.Keys) {
    Write-Host "  '$pattern' -> '$($sensitivePatterns[$pattern])'" -ForegroundColor Gray
}
Write-Host ""

# Check if we're in a Git repository
if (-not (Test-Path ".git")) {
    throw "Not in a Git repository root. Please run from the bootstrap directory."
}

# Check Git status
$gitStatus = git status --porcelain
if ($gitStatus -and -not $Force) {
    Write-Host "⚠️  WARNING: You have uncommitted changes:" -ForegroundColor Red
    git status --short
    Write-Host ""
    Write-Host "Please commit or stash changes before running history cleanup." -ForegroundColor Red
    Write-Host "Or use -Force flag to proceed anyway (not recommended)." -ForegroundColor Red
    exit 1
}

# Show what commits will be affected
Write-Host "Analyzing Git history for sensitive information..." -ForegroundColor Cyan
Write-Host ""

$affectedCommits = @()

foreach ($pattern in $sensitivePatterns.Keys) {
    $commits = git log -S "$pattern" --oneline --all
    if ($commits) {
        Write-Host "Commits containing '$pattern':" -ForegroundColor Yellow
        $commits | ForEach-Object { 
            Write-Host "  $_" -ForegroundColor Gray
            $affectedCommits += ($_ -split ' ')[0]
        }
    }
}

$uniqueCommits = $affectedCommits | Sort-Object -Unique
Write-Host ""
Write-Host "Total affected commits: $($uniqueCommits.Count)" -ForegroundColor Yellow
Write-Host ""

if (-not $Execute) {
    Write-Host "=== DRY RUN MODE ===" -ForegroundColor Green
    Write-Host "This was a dry run. To execute the cleanup, run:" -ForegroundColor Green
    Write-Host "  .\cleanup-history.ps1 -Execute" -ForegroundColor White
    Write-Host ""
    Write-Host "⚠️  WARNING: This operation will rewrite Git history!" -ForegroundColor Red
    Write-Host "- All commit hashes will change" -ForegroundColor Red
    Write-Host "- You'll need to force push to update remote repository" -ForegroundColor Red
    Write-Host "- Anyone with local clones will need to re-clone" -ForegroundColor Red
    Write-Host "- Make sure you have backups before proceeding" -ForegroundColor Red
    exit 0
}

Write-Host "⚠️  EXECUTING HISTORY REWRITE - THIS CANNOT BE EASILY UNDONE!" -ForegroundColor Red
Write-Host ""

# Create a backup tag before we start
$backupTag = "backup-before-history-cleanup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Write-Host "Creating backup tag: $backupTag" -ForegroundColor Green
git tag $backupTag

# Build the sed script for replacements
$sedScript = @()
foreach ($pattern in $sensitivePatterns.Keys) {
    $replacement = $sensitivePatterns[$pattern]
    # Escape special characters for sed
    $escapedPattern = $pattern -replace '([.[\]{}()*+?^$|\\])', '\$1'
    $escapedReplacement = $replacement -replace '([.[\]{}()*+?^$|\\])', '\$1'
    $sedScript += "s/$escapedPattern/$escapedReplacement/g"
}

# Create temporary sed script file
$sedFile = "cleanup-script.sed"
$sedScript | Out-File -FilePath $sedFile -Encoding ASCII

Write-Host "Rewriting Git history..." -ForegroundColor Green
Write-Host "This may take several minutes..." -ForegroundColor Yellow

try {
    # Use git filter-branch to rewrite history
    $env:FILTER_BRANCH_SQUELCH_WARNING = 1
    
    # Create PowerShell filter script for Windows compatibility
    $filterScript = @"
# PowerShell filter script for Windows
`$patterns = @{
    'ExampleOrgU Intune Windows Enterprise Certificate' = 'ENTERPRISE_CERT_PLACEHOLDER'
    'https://cimian.example.com/bootstrap/management.json' = 'https://your-domain.com/bootstrap/management.json'
    'cimian.example.com' = 'your-domain.com'
    'ExampleOrgU' = 'YourOrg'
    'ecuad.ca' = 'your-domain.com'
    '1423F241DFF85AD2C8F31DBD70FB597DAC85BA4B' = 'YOUR_CERTIFICATE_THUMBPRINT_HERE'
    'RODCHRISTIANSEN' = 'EXAMPLE-USER'
    'ca.emilycarru.winadmins' = 'com.yourorg.packages'
}

`$filesToProcess = @('build.ps1', 'installer/Product.wxs', '.env')
foreach (`$file in `$filesToProcess) {
    if (Test-Path `$file) {
        `$content = Get-Content `$file -Raw -ErrorAction SilentlyContinue
        if (`$content) {
            foreach (`$pattern in `$patterns.Keys) {
                `$content = `$content -replace [regex]::Escape(`$pattern), `$patterns[`$pattern]
            }
            Set-Content `$file `$content -NoNewline
        }
    }
}

# Remove sensitive log files
if (Test-Path 'examples/msi-install.log') {
    Remove-Item 'examples/msi-install.log' -Force
}

# Process other files that might contain sensitive information
Get-ChildItem -Recurse -Include '*.log','*.md','*.ps1' | ForEach-Object {
    `$content = Get-Content `$_.FullName -Raw -ErrorAction SilentlyContinue
    if (`$content) {
        `$modified = `$false
        foreach (`$pattern in `$patterns.Keys) {
            if (`$content -match [regex]::Escape(`$pattern)) {
                `$content = `$content -replace [regex]::Escape(`$pattern), `$patterns[`$pattern]
                `$modified = `$true
            }
        }
        if (`$modified) {
            Set-Content `$_.FullName `$content -NoNewline
        }
    }
}
"@

    $filterScript | Out-File -FilePath "filter-script.ps1" -Encoding UTF8
    
    & git filter-branch --tree-filter "powershell.exe -ExecutionPolicy Bypass -File filter-script.ps1" --all

    Write-Host ""
    Write-Host "✅ Git history rewrite completed successfully!" -ForegroundColor Green
    Write-Host ""
    
    # Clean up
    Remove-Item "filter-script.ps1" -Force -ErrorAction SilentlyContinue
    Remove-Item $sedFile -Force -ErrorAction SilentlyContinue
    
    # Show summary
    Write-Host "=== CLEANUP SUMMARY ===" -ForegroundColor Green
    Write-Host "✅ Sensitive information removed from all commits" -ForegroundColor Green
    Write-Host "✅ Backup tag created: $backupTag" -ForegroundColor Green
    Write-Host "✅ History rewrite completed" -ForegroundColor Green
    Write-Host ""
    
    Write-Host "=== NEXT STEPS ===" -ForegroundColor Yellow
    Write-Host "1. Review the changes:" -ForegroundColor White
    Write-Host "   git log --oneline -10" -ForegroundColor Gray
    Write-Host ""
    Write-Host "2. Force push to update remote repository:" -ForegroundColor White
    Write-Host "   git push --force-with-lease origin --all" -ForegroundColor Gray
    Write-Host "   git push --force-with-lease origin --tags" -ForegroundColor Gray
    Write-Host ""
    Write-Host "3. Notify team members to re-clone the repository" -ForegroundColor White
    Write-Host ""
    Write-Host "⚠️  WARNING: All commit hashes have changed!" -ForegroundColor Red
    Write-Host "Anyone with local clones must re-clone the repository." -ForegroundColor Red
    
} catch {
    Write-Host ""
    Write-Host "❌ History rewrite failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "You can restore from backup using:" -ForegroundColor Yellow
    Write-Host "  git reset --hard $backupTag" -ForegroundColor Gray
    throw
} finally {
    # Clean up temporary files
    Remove-Item "filter-script.ps1" -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force ".git/refs/original" -ErrorAction SilentlyContinue
}
