# BootstrapMate Build Script
# Builds, signs, and packages BootstrapMate executable + MSI + .intunewin for deployment
#
# SECURITY NOTE: Code signing is REQUIRED by default for enterprise deployment
# Use -AllowUnsigned only for development builds (NOT for production)
#
# Examples:
#   .\build.ps1                          # Build executables + signed MSI + .intunewin (default)
#   .\build.ps1 -Thumbprint "ABC123..."  # Build with specific certificate
#   .\build.ps1 -AllowUnsigned           # Development build without signing (NOT for production)
#   .\build.ps1 -SkipMSI                 # Build executables only (skip MSI/IntuneWin)

[CmdletBinding()]
param(
    [string]$Thumbprint,
    [ValidateSet("x64", "arm64", "both")]
    [string]$Architecture = "both",
    [switch]$Clean,
    [switch]$Test,
    [switch]$AllowUnsigned,  # Explicit flag to allow unsigned builds for development only
    [switch]$SkipMSI,        # Skip MSI and .intunewin creation (executables only)
    [string]$CimianToolsVersion  # Version to embed in detection registry key
)

$ErrorActionPreference = "Stop"

Write-Host "=== BootstrapMate Build Script ===" -ForegroundColor Magenta
Write-Host "Architecture: $Architecture" -ForegroundColor Yellow
Write-Host "Code Signing: $(if ($AllowUnsigned) { 'DISABLED (Development Only)' } else { 'REQUIRED (Production)' })" -ForegroundColor $(if ($AllowUnsigned) { "Red" } else { "Green" })
Write-Host "MSI + IntuneWin: $(if ($SkipMSI) { 'DISABLED' } else { 'ENABLED (Default)' })" -ForegroundColor $(if ($SkipMSI) { "Yellow" } else { "Green" })
Write-Host "Clean Build: $Clean" -ForegroundColor Yellow
if ($AllowUnsigned) {
    Write-Host ""
    Write-Host "⚠️  WARNING: Building unsigned executable for development only!" -ForegroundColor Red
    Write-Host "   Unsigned builds are NOT suitable for production deployment" -ForegroundColor Red
}
Write-Host ""

# Function to display messages with different log levels
function Write-Log {
    param (
        [string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "SUCCESS")]
        [string]$Level = "INFO"
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    switch ($Level) {
        "INFO"    { Write-Host "[$timestamp] [INFO] $Message" -ForegroundColor White }
        "WARN"    { Write-Host "[$timestamp] [WARN] $Message" -ForegroundColor Yellow }
        "ERROR"   { Write-Host "[$timestamp] [ERROR] $Message" -ForegroundColor Red }
        "SUCCESS" { Write-Host "[$timestamp] [SUCCESS] $Message" -ForegroundColor Green }
    }
}

# Function to check if a command exists
function Test-Command {
    param([string]$Command)
    return [bool](Get-Command $Command -ErrorAction SilentlyContinue)
}

# Function to ensure signtool is available
function Test-SignTool {
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c) { return }
    $roots = @(
        "$env:ProgramFiles\Windows Kits\10\bin",
        "$env:ProgramFiles(x86)\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    try {
        $kitsRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -EA Stop).KitsRoot10
        if ($kitsRoot) { $roots += (Join-Path $kitsRoot 'bin') }
    } catch {}

    foreach ($root in $roots) {
        $cand = Get-ChildItem -Path (Join-Path $root '*\x64\signtool.exe') -EA SilentlyContinue |
                Sort-Object LastWriteTime -Desc | Select-Object -First 1
        if ($cand) {
            $env:Path = "$($cand.Directory.FullName);$env:Path"
            return
        }
    }
    throw "signtool.exe not found. Install Windows 10/11 SDK (Signing Tools)."
}

# Function to find signing certificate
function Get-SigningCertificate {
    param([string]$Thumbprint = $null)
    
    # Check for specific thumbprint from parameter or environment variable
    $certificateThumbprint = $Thumbprint
    if (-not $certificateThumbprint -and $env:CERT_THUMBPRINT) {
        $certificateThumbprint = $env:CERT_THUMBPRINT
        Write-Log "Using certificate thumbprint from environment: $($certificateThumbprint.Substring(0, 8))..." "INFO"
    }
    
    if ($certificateThumbprint) {
        $cert = Get-ChildItem -Path "Cert:\CurrentUser\My\$certificateThumbprint" -ErrorAction SilentlyContinue
        if ($cert) {
            Write-Log "Found certificate by thumbprint: $($cert.Subject)" "SUCCESS"
            return $cert
        }
        Write-Log "Certificate with thumbprint $($certificateThumbprint.Substring(0, 8))... not found" "WARN"
    }
    
    # Search for enterprise certificate by common name from environment variable
    if ($Global:EnterpriseCertCN) {
        $cert = Get-ChildItem -Path "Cert:\CurrentUser\My\" | Where-Object {
            $_.Subject -like "*$Global:EnterpriseCertCN*"
        } | Select-Object -First 1
        
        if ($cert) {
            Write-Log "Found enterprise certificate: $($cert.Subject)" "SUCCESS"
            Write-Log "Thumbprint: $($cert.Thumbprint)" "INFO"
            return $cert
        }
    }
    
    Write-Log "No suitable signing certificate found" "WARN"
    if ($Global:EnterpriseCertCN) {
        Write-Log "Searched for certificate with CN containing: $Global:EnterpriseCertCN" "INFO"
    }
    Write-Log "Set ENTERPRISE_CERT_CN environment variable to your certificate's Common Name" "INFO"
    return $null
}

# Function to sign executable with robust retry and multiple timestamp servers
function Invoke-SignArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Thumbprint,
        [int]$MaxAttempts = 4
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }

    $tsas = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com',
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS'
    )

    $attempt = 0
    while ($attempt -lt $MaxAttempts) {
        $attempt++
        foreach ($tsa in $tsas) {
            & signtool.exe sign `
                /sha1 $Thumbprint `
                /fd SHA256 `
                /td SHA256 `
                /tr $tsa `
                /v `
                "$Path"
            $code = $LASTEXITCODE

            if ($code -eq 0) {
                # Optional append of legacy timestamp for old verifiers; harmless if TSA rejects.
                & signtool.exe timestamp /t http://timestamp.digicert.com /v "$Path" 2>$null
                return
            }

            Start-Sleep -Seconds (4 * $attempt)
        }
    }

    throw "Signing failed after $MaxAttempts attempts across TSAs: $Path"
}

# Function to sign executable
function Invoke-ExecutableSigning {
    param(
        [string]$FilePath,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if (-not (Test-Path $FilePath)) {
        Write-Log "File not found for signing: $FilePath" "ERROR"
        return $false
    }

    Write-Log "Signing executable: $([System.IO.Path]::GetFileName($FilePath))" "INFO"

    # Check if file is locked by trying to open it exclusively
    try {
        $fileStream = [System.IO.File]::Open($FilePath, 'Open', 'Read', 'None')
        $fileStream.Close()
    }
    catch {
        Write-Log "File appears to be locked: $FilePath. Attempting advanced unlock..." "WARN"
        
        # Try to identify and terminate processes locking this file
        try {
            # Use handle.exe if available to identify locking processes
            if (Get-Command "handle.exe" -ErrorAction SilentlyContinue) {
                $handleOutput = & handle.exe $FilePath 2>$null
                if ($handleOutput -and $handleOutput -match "pid: (\d+)") {
                    $lockingPids = [regex]::Matches($handleOutput, "pid: (\d+)") | ForEach-Object { $_.Groups[1].Value }
                    foreach ($procId in $lockingPids) {
                        try {
                            $process = Get-Process -Id $procId -ErrorAction SilentlyContinue
                            if ($process) {
                                Write-Log "Terminating process $($process.Name) (PID: $procId) that may be locking $FilePath" "INFO"
                                $process | Stop-Process -Force -ErrorAction SilentlyContinue
                                Start-Sleep -Seconds 1
                            }
                        }
                        catch {
                            # Ignore errors when killing processes
                        }
                    }
                }
            }
        }
        catch {
            # Ignore handle.exe errors
        }
        
        # Multiple attempts with increasing delays
        $unlockAttempts = 3
        for ($attempt = 1; $attempt -le $unlockAttempts; $attempt++) {
            Start-Sleep -Seconds ($attempt * 2)
            
            # Force garbage collection
            [System.GC]::Collect()
            [System.GC]::WaitForPendingFinalizers()
            [System.GC]::Collect()
            
            try {
                $fileStream = [System.IO.File]::Open($FilePath, 'Open', 'Read', 'None')
                $fileStream.Close()
                Write-Log "File unlocked after $attempt attempts: $FilePath" "SUCCESS"
                break
            }
            catch {
                if ($attempt -eq $unlockAttempts) {
                    Write-Log "File still locked after $unlockAttempts attempts: $FilePath. Skipping signing." "WARN"
                    return $false
                }
            }
        }
    }

    # Check if running as administrator
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isAdmin) {
        Write-Log "Code signing requires administrator privileges. Please run PowerShell as Administrator." "ERROR"
        throw "Access denied: Administrator privileges required for code signing"
    }

    # Use the robust signing function
    try {
        Invoke-SignArtifact -Path $FilePath -Thumbprint $Certificate.Thumbprint
        Write-Log "Successfully signed: $([System.IO.Path]::GetFileName($FilePath))" "SUCCESS"

        # Verify signature
        Write-Log "Verifying signature..." "INFO"
        $null = & signtool verify /pa $FilePath
        if ($LASTEXITCODE -eq 0) {
            Write-Log "Signature verification successful" "SUCCESS"
            return $true
        } else {
            Write-Log "Signature verification failed" "ERROR"
            return $false
        }
    } catch {
        Write-Log "Error during signing: $($_.Exception.Message)" "ERROR"
        return $false
    }
}

# Function to build for specific architecture
function Build-Architecture {
    param(
        [string]$Arch,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCert = $null
    )
    
    Write-Log "Building for $Arch architecture..." "INFO"
    
    $outputDir = "publish\executables\$Arch"
    
    if ($Clean -and (Test-Path $outputDir)) {
        Write-Log "Cleaning output directory: $outputDir" "INFO"
        Remove-Item -Path $outputDir -Recurse -Force
    }
    
    # Ensure output directory exists
    if (-not (Test-Path $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }
    
    # Build arguments
    $buildArgs = @(
        "publish"
        "BootstrapMate.csproj"
        "--configuration", "Release"
        "--runtime", "win-$Arch"
        "--output", $outputDir
        "--self-contained", "true"
        "--verbosity", "minimal"
    )
    
    try {
        Write-Log "Running: dotnet $($buildArgs -join ' ')" "INFO"
        & dotnet @buildArgs
        
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code: $LASTEXITCODE"
        }
        
        $executablePath = Join-Path $outputDir "installapplications.exe"
        
        if (-not (Test-Path $executablePath)) {
            throw "Expected executable not found: $executablePath"
        }
        
        # Convert to absolute path for signing
        $executablePath = (Get-Item $executablePath).FullName
        
        $fileInfo = Get-Item $executablePath
        $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
        Write-Log "Build successful: $($fileInfo.Name) ($sizeMB MB)" "SUCCESS"
        
        # Sign the executable - MANDATORY unless explicitly disabled
        if ($SigningCert) {
            # Check for ARM64 system building x64 - fix ownership issue
            $isARM64System = (Get-WmiObject -Class Win32_Processor | Select-Object -First 1).Architecture -eq 12
            if ($isARM64System -and $Arch -eq "x64") {
                Write-Log "ARM64 system detected - fixing x64 binary ownership for signing..." "INFO"
                try {
                    & takeown /f $executablePath | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Log "Fixed x64 binary ownership" "SUCCESS"
                    }
                } catch {
                    Write-Log "Could not fix ownership, but continuing..." "WARN"
                }
            }
            
            if (Invoke-SignArtifact -Path $executablePath -Thumbprint $SigningCert.Thumbprint) {
                Write-Log "Code signing completed for $Arch" "SUCCESS"
            } else {
                Write-Log "Code signing failed for $Arch" "ERROR"
                return $false
            }
        } else {
            # This should only happen with -AllowUnsigned flag
            Write-Log "UNSIGNED BUILD: This executable is NOT suitable for production deployment" "WARN"
            Write-Log "Unsigned builds should only be used for development and testing" "WARN"
        }
        
        return $true
        
    } catch {
        Write-Log "Build failed for $Arch`: $($_.Exception.Message)" "ERROR"
        return $false
    }
}

# Function to run basic tests
function Test-Build {
    param([string]$ExecutablePath)
    
    Write-Log "Testing build: $ExecutablePath" "INFO"
    
    if (-not (Test-Path $ExecutablePath)) {
        Write-Log "Executable not found for testing: $ExecutablePath" "ERROR"
        return $false
    }
    
    try {
        # Test version output
        Write-Log "Testing --version command..." "INFO"
        $versionOutput = & $ExecutablePath --version 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Log "Version test passed: $versionOutput" "SUCCESS"
        } else {
            Write-Log "Version test failed with exit code: $LASTEXITCODE" "WARN"
        }
        
        # Test help output  
        Write-Log "Testing --help command..." "INFO"
        $null = & $ExecutablePath --help 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Log "Help test passed" "SUCCESS"
        } else {
            Write-Log "Help test failed with exit code: $LASTEXITCODE" "WARN"
        }
        
        return $true
        
    } catch {
        Write-Log "Testing failed: $($_.Exception.Message)" "ERROR"
        return $false
    }
}

# Function to update version in Program.cs
function Update-Version {
    $programCsPath = Join-Path $PSScriptRoot "Program.cs"
    
    if (-not (Test-Path $programCsPath)) {
        throw "Program.cs not found at: $programCsPath"
    }
    
    # Generate full YYYY.MM.DD.HHMM version format for Intune compatibility
    # Note: MSI ProductVersion will use a compatible subset for internal MSI requirements
    $now = Get-Date
    $year = $now.Year          # e.g., 2025
    $month = $now.ToString("MM")     # e.g., 09 for September (zero-padded)  
    $day = $now.ToString("dd")       # e.g., 02 for 2nd day (zero-padded)
    $revision = $now.ToString("HHmm") # e.g., 2141 for 21:41
    
    $newVersion = "$year.$month.$day.$revision"
    Write-Log "Updating version to: $newVersion (YYYY.MM.DD.HHMM format for Intune compatibility)" "INFO"
    
    # Create MSI-compatible version (major.minor.build < 65536)
    # Convert YYYY.MM.DD.HHMM to YY.MM.DD.HHMM for MSI ProductVersion
    $now = Get-Date
    $msiMajor = $now.Year - 2000  # e.g., 25 for 2025
    $msiMinor = [int]$now.ToString("MM")        # e.g., 9 for September  
    $msiBuild = [int]$now.ToString("dd")        # e.g., 2 for 2nd day
    $msiRevision = [int]$now.ToString("HHmm")   # e.g., 2141 for 21:41
    $msiVersion = "$msiMajor.$msiMinor.$msiBuild.$msiRevision"
    
    Write-Log "MSI ProductVersion: $msiVersion (MSI-compliant format)" "INFO"
    
    # Read the current file content
    $content = Get-Content $programCsPath -Raw
    
    # Find and replace the version line using regex
    $pattern = 'private static readonly string Version = "[\d.]+";'
    $replacement = "private static readonly string Version = `"$newVersion`";"
    
    if ($content -match $pattern) {
        $updatedContent = $content -replace $pattern, $replacement
        Set-Content -Path $programCsPath -Value $updatedContent -NoNewline
        Write-Log "Version updated successfully in Program.cs" "SUCCESS"
    } else {
        Write-Log "C# code uses dynamic version generation (as designed)" "INFO"
    }
    
    # Return version information regardless of whether static version was found
    return @{
        FullVersion = $newVersion      # YYYY.MM.DD.HHMM for Intune detection
        MsiVersion = $msiVersion       # YY.MM.DD.HHMM for MSI ProductVersion
    }
}

# Function to build MSI for specific architecture
function Build-MSI {
    param(
        [string]$Arch,
        [string]$Version,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCert = $null,
        [string]$CimianVersion = $null  # Optional CimianTools version for detection
    )
    
    Write-Log "Building MSI for $Arch architecture..." "INFO"
    
    $projectPath = "installer\BootstrapMate.Installer.wixproj"
    
    if (-not (Test-Path $projectPath)) {
        Write-Log "WiX project not found: $projectPath" "ERROR"
        return @{ Success = $false; Architecture = $Arch }
    }
    
    # Get bootstrap URL from environment - REQUIRED
    $bootstrapUrl = $env:BOOTSTRAP_MANIFEST_URL
    if (-not $bootstrapUrl) {
        Write-Log "CRITICAL ERROR: BOOTSTRAP_MANIFEST_URL environment variable not set!" "ERROR"
        Write-Log "Please set this in your .env file or system environment variables" "ERROR"
        Write-Log "Example: BOOTSTRAP_MANIFEST_URL=https://your-domain.com/bootstrap/management.json" "ERROR"
        throw "BOOTSTRAP_MANIFEST_URL environment variable is required for MSI build"
    }
    
    $buildArgs = @(
        "build", $projectPath,
        "--configuration", "Release",
        "--verbosity", "normal",
        "-p:Platform=$Arch",
        "-p:ProductVersion=$($versionInfo.MsiVersion)",
        "-p:BootstrapUrl=$bootstrapUrl"
    )
    
    # Add CimianTools version if provided
    if ($CimianVersion) {
        $buildArgs += "-p:CimianToolsVersion=$CimianVersion"
        Write-Log "Including CimianTools version for detection: $CimianVersion" "INFO"
    }
    
    Write-Log "Building MSI: dotnet $($buildArgs -join ' ')" "INFO"
    
    $process = Start-Process -FilePath "dotnet" -ArgumentList $buildArgs -Wait -PassThru -NoNewWindow
    
    if ($process.ExitCode -eq 0) {
        $msiPath = "installer\bin\$Arch\Release\BootstrapMate-$Arch.msi"
        if (Test-Path $msiPath) {
            Write-Log "MSI built successfully: $msiPath" "SUCCESS"
            
            # Copy MSI to consolidated publish directory
            $publishMsiDir = "publish\msi"
            if (-not (Test-Path $publishMsiDir)) {
                New-Item -ItemType Directory -Path $publishMsiDir -Force | Out-Null
            }
            $finalMsiPath = Join-Path $publishMsiDir "BootstrapMate-$Arch.msi"
            Copy-Item $msiPath $finalMsiPath -Force
            Write-Log "MSI copied to: $finalMsiPath" "INFO"
            
            # Sign MSI if certificate available
            if ($SigningCert) {
                if (Invoke-SignArtifact -Path $finalMsiPath -Thumbprint $SigningCert.Thumbprint) {
                    Write-Log "MSI signed successfully" "SUCCESS"
                } else {
                    Write-Log "MSI signing failed" "ERROR"
                }
            }
            
            return @{ Success = $true; Architecture = $Arch; MsiPath = $finalMsiPath }
        } else {
            Write-Log "MSI build succeeded but file not found: $msiPath" "ERROR"
            return @{ Success = $false; Architecture = $Arch }
        }
    } else {
        Write-Log "MSI build failed for $Arch - dotnet build (WiX) failed with exit code: $($process.ExitCode)" "ERROR"
        return @{ Success = $false; Architecture = $Arch }
    }
}

# Function to create .intunewin packages
function New-IntuneWinPackage {
    param(
        [Parameter(Mandatory)]
        [string]$MsiPath,
        [Parameter(Mandatory)]
        [string]$OutputDirectory
    )
    
    Write-Log "Creating .intunewin package for: $([System.IO.Path]::GetFileName($MsiPath))" "INFO"
    
    # Check for IntuneWinAppUtil.exe and try multiple sources
    $intuneUtilPath = $null
    
    # Try local copy first (downloaded working version)
    $localIntuneUtil = Join-Path $PSScriptRoot "IntuneWinAppUtil.exe"
    if (Test-Path $localIntuneUtil) {
        # Test if the local copy works
        try {
            $testOutput = & $localIntuneUtil -h 2>&1
            if ($LASTEXITCODE -eq 0) {
                $intuneUtilPath = $localIntuneUtil
                Write-Log "Using local IntuneWinAppUtil.exe (verified working)" "SUCCESS"
            } else {
                Write-Log "Local IntuneWinAppUtil.exe exists but doesn't work properly" "WARNING"
            }
        } catch {
            Write-Log "Local IntuneWinAppUtil.exe test failed: $($_.Exception.Message)" "WARNING"
        }
    }
    
    # Try system PATH if local copy doesn't work
    if (-not $intuneUtilPath -and (Test-Command "IntuneWinAppUtil.exe")) {
        try {
            $testOutput = & IntuneWinAppUtil.exe -h 2>&1
            if ($LASTEXITCODE -eq 0) {
                $intuneUtilPath = "IntuneWinAppUtil.exe"
                Write-Log "Using system IntuneWinAppUtil.exe (verified working)" "SUCCESS"
            } else {
                Write-Log "System IntuneWinAppUtil.exe exists but doesn't work properly" "WARNING"
            }
        } catch {
            Write-Log "System IntuneWinAppUtil.exe test failed: $($_.Exception.Message)" "WARNING"
        }
    }
    
    # Download working version if needed
    if (-not $intuneUtilPath) {
        Write-Log "No working IntuneWinAppUtil.exe found. Downloading from Microsoft..." "INFO"
        try {
            $downloadUrl = "https://raw.githubusercontent.com/microsoft/Microsoft-Win32-Content-Prep-Tool/master/IntuneWinAppUtil.exe"
            $intuneUtilPath = $localIntuneUtil
            Invoke-WebRequest -Uri $downloadUrl -OutFile $intuneUtilPath -UseBasicParsing
            
            # Test the downloaded version
            $testOutput = & $intuneUtilPath -h 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Log "Downloaded and verified working IntuneWinAppUtil.exe" "SUCCESS"
            } else {
                throw "Downloaded IntuneWinAppUtil.exe doesn't work properly"
            }
        } catch {
            Write-Log "Failed to download working IntuneWinAppUtil.exe: $($_.Exception.Message)" "ERROR"
            return $null
        }
    }
    
    $setupFolder = Split-Path $MsiPath -Parent
    $outputFolder = "publish\intunewin"
    
    # Ensure output directory exists
    if (-not (Test-Path $outputFolder)) {
        New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
    }
    
    # Remove existing .intunewin files to prevent conflicts
    $msiBaseName = [System.IO.Path]::GetFileNameWithoutExtension($MsiPath)
    $existingIntunewin = Get-ChildItem -Path $outputFolder -Filter "$msiBaseName.intunewin" -ErrorAction SilentlyContinue
    if ($existingIntunewin) {
        Write-Log "Removing existing .intunewin file to prevent conflicts" "INFO"
        Remove-Item -Path $existingIntunewin.FullName -Force
    }
    
    # Create the .intunewin package
    $intuneArgs = @(
        "-c", "`"$setupFolder`""
        "-s", "`"$MsiPath`""
        "-o", "`"$outputFolder`""
        "-q"  # Quiet mode
    )
    
    Write-Log "Running: $intuneUtilPath $($intuneArgs -join ' ')" "INFO"
    
    try {
        # Run the actual packaging
        $process = Start-Process -FilePath $intuneUtilPath -ArgumentList $intuneArgs -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput "$env:TEMP\intunewin-out.txt" -RedirectStandardError "$env:TEMP\intunewin-err.txt"
        
        if ($process.ExitCode -eq 0) {
            $expectedIntuneWin = Join-Path $outputFolder "$msiBaseName.intunewin"
            if (Test-Path $expectedIntuneWin) {
                $fileSize = (Get-Item $expectedIntuneWin).Length
                $sizeMB = [math]::Round($fileSize / 1MB, 2)
                Write-Log ".intunewin package created successfully: $expectedIntuneWin ($sizeMB MB)" "SUCCESS"
                return $expectedIntuneWin
            } else {
                Write-Log ".intunewin package creation succeeded but file not found: $expectedIntuneWin" "ERROR"
                return $null
            }
        } else {
            $errorOutput = ""
            if (Test-Path "$env:TEMP\intunewin-err.txt") {
                $errorOutput = Get-Content "$env:TEMP\intunewin-err.txt" -Raw
            }
            Write-Log "IntuneWinAppUtil failed with exit code: $($process.ExitCode)" "ERROR"
            if ($errorOutput) {
                Write-Log "Error details: $errorOutput" "ERROR"
            }
            return $null
        }
    } catch {
        Write-Log "Error running IntuneWinAppUtil: $($_.Exception.Message)" "ERROR"
        return $null
    } finally {
        # Clean up temp files
        Remove-Item "$env:TEMP\intunewin-out.txt" -Force -ErrorAction SilentlyContinue
        Remove-Item "$env:TEMP\intunewin-err.txt" -Force -ErrorAction SilentlyContinue
    }
}

# Import environment variables from .env file if it exists
function Import-EnvironmentVariables {
    $envFile = Join-Path $PSScriptRoot ".env"
    if (Test-Path $envFile) {
        Write-Log "Loading environment variables from .env file" "INFO"
        Get-Content $envFile | Where-Object { $_ -notmatch '^#' -and $_ -notmatch '^\s*$' } | ForEach-Object {
            $name, $value = $_ -split '=', 2
            if ($name -and $value) {
                $name = $name.Trim()
                $value = $value.Trim().Trim('"').Trim("'")
                [Environment]::SetEnvironmentVariable($name, $value, [EnvironmentVariableTarget]::Process)
                Write-Log "Loaded environment variable: $name" "INFO"
            }
        }
    } else {
        Write-Log "No .env file found. Using system environment variables only." "INFO"
    }
}

# Main build process
try {
    $rootPath = $PSScriptRoot
    Push-Location $rootPath
    
    # Load environment variables first
    Import-EnvironmentVariables

    # Enterprise Certificate Configuration - REQUIRED environment variable
    $Global:EnterpriseCertCN = $env:ENTERPRISE_CERT_CN
    if (-not $Global:EnterpriseCertCN) {
        Write-Log "CRITICAL ERROR: ENTERPRISE_CERT_CN environment variable not set!" "ERROR"
        Write-Log "Please set this in your .env file or system environment variables" "ERROR"
        Write-Log "Example: ENTERPRISE_CERT_CN=Your Organization Code Signing Certificate" "ERROR"
        Write-Log "" "ERROR"
        Write-Log "Create a .env file with:" "ERROR"
        Write-Log "ENTERPRISE_CERT_CN=Your Organization Code Signing Certificate" "ERROR"
        throw "ENTERPRISE_CERT_CN environment variable is required for certificate discovery"
    }
    
    # Update version first
    $versionInfo = Update-Version
    if ($versionInfo -and $versionInfo.FullVersion) {
        Write-Log "Building with version: $($versionInfo.FullVersion)" "INFO"
        Write-Log "MSI ProductVersion: $($versionInfo.MsiVersion)" "INFO"
    }
    
    # Prerequisites check
    Write-Log "Checking prerequisites..." "INFO"
    
    if (-not (Test-Command "dotnet")) {
        throw ".NET CLI not found. Please install .NET 8 SDK."
    }
    
    $dotnetVersion = & dotnet --version
    Write-Log "Using .NET version: $dotnetVersion" "INFO"
    
    # Check MSI prerequisites if not skipping
    if (-not $SkipMSI) {
        Write-Log "Checking MSI build prerequisites..." "INFO"
        
        # Verify WiX project exists
        $wixProject = "installer\BootstrapMate.Installer.wixproj"
        if (-not (Test-Path $wixProject)) {
            Write-Log "WiX project not found: $wixProject" "ERROR"
            Write-Log "MSI building requires WiX project structure" "ERROR"
            throw "WiX project missing - cannot build MSI packages"
        }
        
        # Check for Chocolatey (for IntuneWinAppUtil installation)
        if (-not (Test-Command "choco")) {
            Write-Log "Chocolatey not found. Installing for tool management..." "INFO"
            try {
                Set-ExecutionPolicy Bypass -Scope Process -Force
                [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
                Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
                Write-Log "Chocolatey installed successfully" "SUCCESS"
                # Add to PATH for this session
                $env:Path += ";$env:ALLUSERSPROFILE\chocolatey\bin"
            } catch {
                Write-Log "Failed to install Chocolatey: $($_.Exception.Message)" "WARN"
                Write-Log "IntuneWinAppUtil may need manual installation" "WARN"
            }
        }
        
        Write-Log "MSI prerequisites check completed" "SUCCESS"
    }
    
    # Handle signing certificate - Code signing is REQUIRED unless explicitly disabled
    $signingCert = $null
    $requireSigning = -not $AllowUnsigned
    
    if ($requireSigning) {
        Write-Log "Code signing is REQUIRED for production builds" "INFO"
        Test-SignTool
        $signingCert = Get-SigningCertificate -Thumbprint $Thumbprint
        if (-not $signingCert) {
            Write-Log "CRITICAL ERROR: Code signing certificate not found!" "ERROR"
            Write-Log "BootstrapMate MUST be signed for enterprise deployment" "ERROR"
            Write-Log "" "ERROR"
            Write-Log "Solutions:" "ERROR"
            Write-Log "1. Install your enterprise code signing certificate" "ERROR"
            Write-Log "2. Specify certificate thumbprint with -Thumbprint parameter" "ERROR"
            Write-Log "3. For development only: use -AllowUnsigned flag (NOT for production)" "ERROR"
            throw "Code signing certificate required but not found. Cannot build unsigned executable for production."
        }
        Write-Log "Code signing certificate found and verified" "SUCCESS"
    } else {
        Write-Log "WARNING: Building unsigned executable for development only" "WARN"
        Write-Log "NEVER deploy unsigned builds to production environments" "WARN"
    }
    
    # Build for requested architectures
    $buildResults = @()
    
    $architectures = switch ($Architecture) {
        "x64"  { @("x64") }
        "arm64" { @("arm64") }
        "both" { @("x64", "arm64") }
    }
    
    foreach ($arch in $architectures) {
        Write-Log "" "INFO"
        $success = Build-Architecture -Arch $arch -SigningCert $signingCert
        $buildResults += @{
            Architecture = $arch
            Success = $success
            Path = "publish\executables\$arch\installapplications.exe"
        }
        
        if ($Test -and $success) {
            $execPath = Join-Path $rootPath "publish\executables\$arch\installapplications.exe"
            Test-Build -ExecutablePath $execPath
        }
    }
    
    # Build MSI packages and .intunewin files (enterprise default)
    $msiResults = @()
    $intuneWinResults = @()
    
    if (-not $SkipMSI) {
        Write-Log "" "INFO"
        Write-Log "=== MSI + INTUNEWIN BUILD ===" "INFO"
        
        # Prerequisites for MSI building
        if (-not (Test-Command "dotnet")) {
            Write-Log ".NET CLI not found. Please install .NET 8.0 SDK" "ERROR"
        } else {
            # Build MSI for each successful executable architecture
            $publishRoot = Join-Path $rootPath "publish"
            if ($Clean) {
                # Clean only MSI and IntuneWin outputs, not executables
                $msiDir = Join-Path $publishRoot "msi"
                $intuneDir = Join-Path $publishRoot "intunewin"
                if (Test-Path $msiDir) {
                    Write-Log "Cleaning MSI directory: $msiDir" "INFO"
                    Remove-Item -Path $msiDir -Recurse -Force
                }
                if (Test-Path $intuneDir) {
                    Write-Log "Cleaning IntuneWin directory: $intuneDir" "INFO"
                    Remove-Item -Path $intuneDir -Recurse -Force
                }
            }
            
            # Ensure publish root directory exists
            if (-not (Test-Path $publishRoot)) {
                New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
            }
            
            foreach ($result in $buildResults) {
                if ($result.Success) {
                    Write-Log "" "INFO"
                    $msiResult = Build-MSI -Arch $result.Architecture -Version $versionInfo.MsiVersion -SigningCert $signingCert -CimianVersion $CimianToolsVersion
                    $msiResults += $msiResult
                    
                    # Create .intunewin if MSI was successful
                    if ($msiResult.Success -and $msiResult.MsiPath) {
                        $fullMsiPath = (Get-Item $msiResult.MsiPath).FullName
                        $intuneWinPath = New-IntuneWinPackage -MsiPath $fullMsiPath -OutputDirectory "publish\intunewin"
                        if ($intuneWinPath) {
                            $intuneWinResults += @{
                                Architecture = $result.Architecture
                                Success = $true
                                IntuneWinPath = $intuneWinPath
                            }
                        } else {
                            $intuneWinResults += @{
                                Architecture = $result.Architecture
                                Success = $false
                            }
                        }
                    }
                }
            }
        }
    } else {
        Write-Log "MSI and .intunewin creation skipped (-SkipMSI flag)" "INFO"
    }
    
    # Build summary
    Write-Log "" "INFO"
    Write-Log "=== BUILD SUMMARY ===" "INFO"
    
    $successCount = 0
    $signedCount = 0
    foreach ($result in $buildResults) {
        if ($result.Success) {
            $successCount++
            $fullPath = Join-Path $rootPath $result.Path
            if (Test-Path $fullPath) {
                $fileInfo = Get-Item $fullPath
                $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
                
                # Check if file is signed
                $isSigned = $false
                if ($signingCert) {
                    try {
                        $signature = Get-AuthenticodeSignature -FilePath $fullPath
                        $isSigned = ($signature.Status -eq "Valid")
                        if ($isSigned) { $signedCount++ }
                    } catch {
                        $isSigned = $false
                    }
                }
                
                $signStatus = if ($signingCert) { 
                    if ($isSigned) { " [SIGNED ✓]" } else { " [SIGN FAILED ❌]" } 
                } else { 
                    " [UNSIGNED ⚠️ DEV ONLY]" 
                }
                Write-Log "✅ $($result.Architecture): $($result.Path) ($sizeMB MB)$signStatus" "SUCCESS"
            } else {
                Write-Log "✅ $($result.Architecture): Built successfully" "SUCCESS"
            }
        } else {
            Write-Log "❌ $($result.Architecture): Build failed" "ERROR"
        }
    }
    
    # MSI Summary
    if (-not $SkipMSI) {
        Write-Log "" "INFO"
        Write-Log "=== MSI SUMMARY ===" "INFO"
        $msiSuccessCount = 0
        $msiTotalCount = $msiResults.Count
        
        foreach ($msiResult in $msiResults) {
            if ($msiResult.Success) { $msiSuccessCount++ }
        }
        
        foreach ($msiResult in $msiResults) {
            if ($msiResult.Success) {
                $fileName = Split-Path $msiResult.MsiPath -Leaf
                $fileSize = (Get-Item $msiResult.MsiPath).Length
                $sizeMB = [math]::Round($fileSize / 1MB, 2)
                $signStatus = if ($signingCert) { " [SIGNED]" } else { " [UNSIGNED]" }
                Write-Log "SUCCESS $($msiResult.Architecture): $fileName ($sizeMB MB)$signStatus" "SUCCESS"
            } else {
                Write-Log "ERROR $($msiResult.Architecture): MSI build failed" "ERROR"
            }
        }
        
        Write-Log "" "INFO"
        Write-Log "=== INTUNEWIN SUMMARY ===" "INFO"
        $intuneSuccessCount = 0
        $intuneTotalCount = $intuneWinResults.Count
        
        foreach ($intuneResult in $intuneWinResults) {
            if ($intuneResult.Success) { $intuneSuccessCount++ }
        }
        
        foreach ($intuneResult in $intuneWinResults) {
            if ($intuneResult.Success) {
                $fileName = Split-Path $intuneResult.IntuneWinPath -Leaf
                $fileSize = (Get-Item $intuneResult.IntuneWinPath).Length
                $sizeMB = [math]::Round($fileSize / 1MB, 2)
                Write-Log "SUCCESS $($intuneResult.Architecture): $fileName ($sizeMB MB)" "SUCCESS"
            } else {
                Write-Log "ERROR $($intuneResult.Architecture): .intunewin creation failed" "ERROR"
            }
        }
        
        Write-Log "" "INFO"
        Write-Log "MSI Packages: $msiSuccessCount/$msiTotalCount successful" "INFO"
        Write-Log ".intunewin Packages: $intuneSuccessCount/$intuneTotalCount successful" "INFO"
    }
    
    Write-Log "" "INFO"
    Write-Log "Executable Builds: $successCount of $($buildResults.Count) architectures successfully" "INFO"
    
    if ($signingCert) {
        if ($signedCount -eq $successCount) {
            Write-Log "All executables signed with certificate: $($signingCert.Subject)" "SUCCESS"
        } else {
            Write-Log "Signing completed for $signedCount of $successCount executables" "WARN"
            Write-Log "Certificate: $($signingCert.Subject)" "INFO"
        }
    }
    
    # Overall success determination
    $overallSuccess = $successCount -eq $buildResults.Count
    if (-not $SkipMSI) {
        # Fix PowerShell array handling by using proper filtering
        $msiSuccessfulCount = 0
        $intuneSuccessfulCount = 0
        
        foreach ($msi in $msiResults) {
            if ($msi.Success -eq $true) { $msiSuccessfulCount++ }
        }
        
        foreach ($intune in $intuneWinResults) {
            if ($intune.Success -eq $true) { $intuneSuccessfulCount++ }
        }
        
        $msiAllSuccess = $msiSuccessfulCount -eq $msiResults.Count
        $intuneAllSuccess = $intuneSuccessfulCount -eq $intuneWinResults.Count
        $overallSuccess = $overallSuccess -and $msiAllSuccess -and $intuneAllSuccess
    }
    
    if ($overallSuccess) {
        Write-Log "" "SUCCESS"
        Write-Log "🎉 ALL BUILDS COMPLETED SUCCESSFULLY!" "SUCCESS"
        if (-not $SkipMSI) {
            $signStatus = if ($AllowUnsigned) { "built (unsigned)" } else { "built and signed" }
            $deploymentStatus = if ($AllowUnsigned) { "development testing" } else { "enterprise deployment" }
            
            Write-Log "✅ Executables $signStatus" "SUCCESS"
            Write-Log "✅ MSI packages created$(if (-not $AllowUnsigned) { ' and signed' })" "SUCCESS"
            Write-Log "✅ .intunewin packages ready for Intune deployment" "SUCCESS"
            Write-Log "" "INFO"
            Write-Log "📦 Ready for $deploymentStatus!" "SUCCESS"
            
            if ($AllowUnsigned) {
                Write-Log "" "WARN"
                Write-Log "⚠️  REMINDER: This is an UNSIGNED build for development only!" "WARN"
                Write-Log "   Do NOT deploy to production environments" "WARN"
            }
        }
        exit 0
    } else {
        Write-Log "Some builds failed" "ERROR"
        exit 1
    }
    
} catch {
    Write-Log "Build process failed: $($_.Exception.Message)" "ERROR"
    exit 1
} finally {
    Pop-Location
}
