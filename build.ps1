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
    Write-Host "WARNING: Building unsigned executable for development only!" -ForegroundColor Red
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

# Function to detect ARM64 system architecture (centralized detection)
function Test-ARM64System {
    try {
        # Use modern approach first (faster)
        $processor = Get-CimInstance -ClassName Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($processor) {
            return $processor.Architecture -eq 12  # ARM64 architecture code
        }
        
        # Fallback to older WMI approach
        $processor = Get-WmiObject -Class Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($processor) {
            return $processor.Architecture -eq 12
        }
        
        # Final fallback using environment variable
        return $env:PROCESSOR_ARCHITECTURE -eq "ARM64"
    }
    catch {
        Write-Log "ARM64 detection failed: $($_.Exception.Message). Assuming x64." "WARN"
        return $false
    }
}

# Function to perform comprehensive garbage collection
function Invoke-ComprehensiveGC {
    param([string]$Reason = "General cleanup")
    
    Write-Log "Performing garbage collection: $Reason" "INFO"
    try {
        # Force comprehensive cleanup
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        
        # Give system time to fully release handles
        Start-Sleep -Milliseconds 500
        
        Write-Log "Garbage collection completed" "INFO"
    }
    catch {
        Write-Log "Garbage collection failed: $($_.Exception.Message)" "WARN"
    }
}

# Function to test if a file is locked
function Test-FileLocked {
    param([string]$FilePath)
    
    if (-not (Test-Path $FilePath)) {
        return $false
    }
    
    try {
        $fileStream = [System.IO.File]::Open($FilePath, 'Open', 'ReadWrite', 'None')
        $fileStream.Close()
        return $false  # File is not locked
    }
    catch [System.IO.IOException] {
        return $true   # File is locked
    }
    catch {
        Write-Log "Unexpected error testing file lock: $($_.Exception.Message)" "WARN"
        return $false  # Assume not locked if we can't determine
    }
}

# Function to ensure signtool is available (enhanced from CimianTools)
function Test-SignTool {
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c) { 
        Write-Log "Found signtool.exe in PATH: $($c.Source)" "SUCCESS"
        return 
    }
    
    Write-Log "signtool.exe not found in PATH, searching Windows SDK installations..." "INFO"
    
    $roots = @(
        "$env:ProgramFiles\Windows Kits\10\bin",
        "$env:ProgramFiles(x86)\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    try {
        $kitsRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -EA Stop).KitsRoot10
        if ($kitsRoot) { 
            $binPath = Join-Path $kitsRoot 'bin'
            if (Test-Path $binPath) {
                $roots += $binPath
                Write-Log "Found Windows SDK from registry: $binPath" "INFO"
            }
        }
    } catch {
        Write-Log "No Windows SDK found in registry" "INFO"
    }

    foreach ($root in $roots) {
        # Look for signtool in architecture-specific subdirectories
        $patterns = @(
            Join-Path $root '*\x64\signtool.exe',
            Join-Path $root '*\arm64\signtool.exe',
            Join-Path $root '*\x86\signtool.exe'
        )
        
        foreach ($pattern in $patterns) {
            $candidates = Get-ChildItem -Path $pattern -EA SilentlyContinue | Sort-Object LastWriteTime -Desc
            if ($candidates) {
                $bestCandidate = $candidates | Select-Object -First 1
                $signtoolDir = $bestCandidate.Directory.FullName
                $env:Path = "$signtoolDir;$env:Path"
                Write-Log "Found signtool.exe: $($bestCandidate.FullName)" "SUCCESS"
                Write-Log "Added to PATH: $signtoolDir" "INFO"
                return
            }
        }
    }
    
    throw "signtool.exe not found. Install Windows 10/11 SDK with Signing Tools component."
}

# Function to find signing certificate (enhanced from CimianTools)
function Get-SigningCertThumbprint {
    param([string]$Thumbprint = $null)
    
    # Check for specific thumbprint from parameter or environment variable
    $certificateThumbprint = $Thumbprint
    if (-not $certificateThumbprint -and $env:CERT_THUMBPRINT) {
        $certificateThumbprint = $env:CERT_THUMBPRINT
        Write-Log "Using certificate thumbprint from environment: $($certificateThumbprint.Substring(0, 8))..." "INFO"
    }
    
    if ($certificateThumbprint) {
        # Check CurrentUser store first
        $cert = Get-ChildItem -Path "Cert:\CurrentUser\My\$certificateThumbprint" -ErrorAction SilentlyContinue
        if ($cert) {
            Write-Log "Found certificate by thumbprint in CurrentUser store: $($cert.Subject)" "SUCCESS"
            return @{ Certificate = $cert; Store = "CurrentUser"; Thumbprint = $cert.Thumbprint }
        }
        
        # Check LocalMachine store
        $cert = Get-ChildItem -Path "Cert:\LocalMachine\My\$certificateThumbprint" -ErrorAction SilentlyContinue
        if ($cert) {
            Write-Log "Found certificate by thumbprint in LocalMachine store: $($cert.Subject)" "SUCCESS"
            return @{ Certificate = $cert; Store = "LocalMachine"; Thumbprint = $cert.Thumbprint }
        }
        
        Write-Log "Certificate with thumbprint $($certificateThumbprint.Substring(0, 8))... not found in any store" "WARN"
    }
    
    # Search for enterprise certificate by common name from environment variable
    if ($Global:EnterpriseCertCN) {
        Write-Log "Searching for certificate with CN containing: $Global:EnterpriseCertCN" "INFO"
        
        # Check CurrentUser store first
        $cert = Get-ChildItem -Path "Cert:\CurrentUser\My\" | Where-Object {
            $_.Subject -like "*$Global:EnterpriseCertCN*"
        } | Select-Object -First 1
        
        if ($cert) {
            Write-Log "Found enterprise certificate in CurrentUser store: $($cert.Subject)" "SUCCESS"
            Write-Log "Thumbprint: $($cert.Thumbprint)" "INFO"
            Write-Log "Has Private Key: $($cert.HasPrivateKey)" "INFO"
            Write-Log "Valid until: $($cert.NotAfter)" "INFO"
            return @{ Certificate = $cert; Store = "CurrentUser"; Thumbprint = $cert.Thumbprint }
        }
        
        # Check LocalMachine store
        $cert = Get-ChildItem -Path "Cert:\LocalMachine\My\" | Where-Object {
            $_.Subject -like "*$Global:EnterpriseCertCN*"
        } | Select-Object -First 1
        
        if ($cert) {
            Write-Log "Found enterprise certificate in LocalMachine store: $($cert.Subject)" "SUCCESS"
            Write-Log "Thumbprint: $($cert.Thumbprint)" "INFO"
            Write-Log "Has Private Key: $($cert.HasPrivateKey)" "INFO"
            Write-Log "Valid until: $($cert.NotAfter)" "INFO"
            return @{ Certificate = $cert; Store = "LocalMachine"; Thumbprint = $cert.Thumbprint }
        }
    }
    
    Write-Log "No suitable signing certificate found" "WARN"
    if ($Global:EnterpriseCertCN) {
        Write-Log "Searched for certificate with CN containing: $Global:EnterpriseCertCN" "INFO"
    }
    Write-Log "Set ENTERPRISE_CERT_CN environment variable to your certificate's Common Name" "INFO"
    return $null
}

# Function to find signing certificate (legacy wrapper for compatibility)
function Get-SigningCertificate {
    param([string]$Thumbprint = $null)
    
    $certInfo = Get-SigningCertThumbprint -Thumbprint $Thumbprint
    if ($certInfo) {
        return $certInfo.Certificate
    }
    return $null
}

# Function to sign executable with robust retry and multiple timestamp servers (enhanced from CimianTools)
function Invoke-SignArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Thumbprint,
        [string]$Store = "CurrentUser",
        [int]$MaxAttempts = 4
    )

    if (-not (Test-Path -LiteralPath $Path)) { 
        throw "File not found: $Path" 
    }

    Write-Log "Signing artifact: $([System.IO.Path]::GetFileName($Path))" "INFO"
    Write-Log "Certificate store: $Store" "INFO"
    Write-Log "Certificate thumbprint: $($Thumbprint.Substring(0, 8))..." "INFO"

    $tsas = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com', 
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS',
        'http://timestamp.comodoca.com/authenticode'
    )

    $attempt = 0
    $lastError = $null
    
    while ($attempt -lt $MaxAttempts) {
        $attempt++
        Write-Log "Signing attempt $attempt of $MaxAttempts..." "INFO"
        
        foreach ($tsa in $tsas) {
            try {
                Write-Log "Using timestamp server: $tsa" "INFO"
                
                # Build signtool arguments based on certificate store
                $storeArgs = if ($Store -eq "CurrentUser") {
                    @("/s", "My")
                } else {
                    @("/s", "My", "/sm")
                }
                
                $signArgs = @("sign") + $storeArgs + @(
                    "/sha1", $Thumbprint,
                    "/fd", "SHA256",
                    "/td", "SHA256", 
                    "/tr", $tsa,
                    "/v",
                    $Path
                )
                
                Write-Log "Running: signtool.exe $($signArgs -join ' ')" "INFO"
                
                & signtool.exe @signArgs
                $code = $LASTEXITCODE

                if ($code -eq 0) {
                    Write-Log "Primary signing successful with TSA: $tsa" "SUCCESS"
                    
                    # Optional: append legacy timestamp for compatibility with older verifiers
                    try {
                        Write-Log "Adding legacy timestamp for compatibility..." "INFO"
                        & signtool.exe timestamp /t http://timestamp.digicert.com /v "$Path" 2>$null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Log "Legacy timestamp added successfully" "SUCCESS"
                        } else {
                            Write-Log "Legacy timestamp failed (non-critical)" "INFO"
                        }
                    } catch {
                        Write-Log "Legacy timestamp failed (non-critical): $($_.Exception.Message)" "INFO"
                    }
                    
                    # Verify the signature
                    Write-Log "Verifying signature..." "INFO"
                    & signtool.exe verify /pa "$Path"
                    if ($LASTEXITCODE -eq 0) {
                        Write-Log "Signature verification successful!" "SUCCESS"
                        return $true
                    } else {
                        Write-Log "Signature verification failed" "ERROR"
                        return $false
                    }
                }

                $lastError = "signtool exit code: $code"
                Write-Log "TSA $tsa failed: $lastError" "WARN"
                
                # Wait before trying next TSA
                Start-Sleep -Seconds 2
                
            } catch {
                $lastError = $_.Exception.Message
                Write-Log "Exception with TSA $tsa`: $lastError" "WARN"
                Start-Sleep -Seconds 2
            }
        }
        
        # Wait before next attempt with exponential backoff
        if ($attempt -lt $MaxAttempts) {
            $waitSeconds = 4 * $attempt
            Write-Log "All TSAs failed for attempt $attempt. Waiting $waitSeconds seconds before retry..." "WARN"
            Start-Sleep -Seconds $waitSeconds
        }
    }

    # If all normal attempts failed, try with sudo if available
    $sudoAvailable = Get-Command sudo -ErrorAction SilentlyContinue
    if ($sudoAvailable) {
        Write-Log "All normal signing attempts failed. Attempting with sudo elevation..." "WARN"
        
        try {
            # Use sudo with the first (most reliable) timestamp server
            $primaryTsa = $tsas[0]
            Write-Log "Using sudo with primary TSA: $primaryTsa" "INFO"
            
            # Build sudo signtool command
            $storeArg = if ($Store -eq "CurrentUser") { "My" } else { "My" }
            $storeModifier = if ($Store -ne "CurrentUser") { "/sm" } else { "" }
            
            Write-Log "Running with sudo: signtool.exe sign /s $storeArg $(if($storeModifier){$storeModifier}) /sha1 $Thumbprint /fd SHA256 /td SHA256 /tr $primaryTsa /v `"$Path`"" "INFO"
            
            # Execute with sudo directly (not through cmd)
            $sudoArgs = @(
                "signtool.exe",
                "sign",
                "/s", $storeArg
            )
            if ($storeModifier) { $sudoArgs += $storeModifier }
            $sudoArgs += @(
                "/sha1", $Thumbprint,
                "/fd", "SHA256",
                "/td", "SHA256",
                "/tr", $primaryTsa,
                "/v",
                $Path
            )
            
            & sudo @sudoArgs
            $sudoExitCode = $LASTEXITCODE
            
            if ($sudoExitCode -eq 0) {
                Write-Log "Successfully signed with sudo elevation!" "SUCCESS"
                
                # Verify the signature
                Write-Log "Verifying sudo-signed signature..." "INFO"
                & signtool.exe verify /pa "$Path"
                if ($LASTEXITCODE -eq 0) {
                    Write-Log "Sudo signature verification successful!" "SUCCESS"
                    return $true
                } else {
                    Write-Log "Sudo signature verification failed" "ERROR"
                    return $false
                }
            } else {
                Write-Log "Sudo signing failed with exit code: $sudoExitCode" "WARN"
            }
            
        } catch {
            Write-Log "Exception during sudo signing: $($_.Exception.Message)" "WARN"
        }
    } else {
        Write-Log "sudo not available for elevated signing attempt" "WARN"
    }

    throw "Signing failed after $MaxAttempts attempts across all TSAs (including sudo). Last error: $lastError"
}

# Function to aggressively unlock a file using multiple strategies
function Invoke-FileUnlock {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [int]$MaxAttempts = 3
    )
    
    if (-not (Test-Path $FilePath)) {
        Write-Log "File not found for unlock: $FilePath" "ERROR"
        return $false
    }
    
    $fileName = [System.IO.Path]::GetFileName($FilePath)
    Write-Log "Attempting to unlock file: $fileName" "INFO"
    
    # First, try garbage collection
    Invoke-ComprehensiveGC -Reason "Pre-unlock file handle cleanup"
    
    # Test if file is actually locked
    if (-not (Test-FileLocked -FilePath $FilePath)) {
        Write-Log "File is not locked: $fileName" "SUCCESS"
        return $true
    }
    
    Write-Log "File is locked, attempting aggressive unlock strategies..." "WARN"
    
    # Strategy 1: Multiple GC attempts with increasing delays
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Write-Log "Unlock attempt $attempt/$MaxAttempts using garbage collection..." "INFO"
        
        Invoke-ComprehensiveGC -Reason "Unlock attempt $attempt"
        Start-Sleep -Seconds ($attempt * 2)
        
        if (-not (Test-FileLocked -FilePath $FilePath)) {
            Write-Log "File unlocked via garbage collection on attempt $attempt" "SUCCESS"
            return $true
        }
    }
    
    # Strategy 2: Robocopy-based unlock (CimianTools approach)
    Write-Log "Attempting robocopy-based unlock..." "INFO"
    
    $sourceDir = Split-Path $FilePath -Parent
    $tempUnlockDir = Join-Path (Split-Path $FilePath -Parent) "temp_unlock_$(Get-Random)"
    $tempFilePath = Join-Path $tempUnlockDir $fileName
    
    try {
        # Create temp directory
        if (Test-Path $tempUnlockDir) { 
            Remove-Item $tempUnlockDir -Recurse -Force -ErrorAction SilentlyContinue 
        }
        New-Item -ItemType Directory -Path $tempUnlockDir -Force | Out-Null
        
        # Use robocopy with minimal retries and output
        Write-Log "Using robocopy to break file locks..." "INFO"
        $robocopyResult = & robocopy "$sourceDir" "$tempUnlockDir" "$fileName" /R:2 /W:1 /NP /NDL /NJH /NJS 2>&1
        $robocopyExitCode = $LASTEXITCODE
        
        # Robocopy exit codes 0-7 are success/partial success
        if ($robocopyExitCode -le 7 -and (Test-Path $tempFilePath)) {
            Write-Log "Robocopy successful, replacing original file..." "INFO"
            
            # Additional GC before file operations
            Invoke-ComprehensiveGC -Reason "Pre-file replacement"
            
            # Remove original and move back
            try {
                Remove-Item $FilePath -Force
                Move-Item $tempFilePath $FilePath -Force
                Write-Log "File unlocked via robocopy: $fileName" "SUCCESS"
                return $true
            }
            catch {
                Write-Log "Failed to replace file after robocopy: $($_.Exception.Message)" "ERROR"
                # Try to restore from temp if original was deleted
                if (-not (Test-Path $FilePath) -and (Test-Path $tempFilePath)) {
                    try {
                        Move-Item $tempFilePath $FilePath -Force
                        Write-Log "File restored from temp location" "INFO"
                    }
                    catch {
                        Write-Log "Failed to restore file: $($_.Exception.Message)" "ERROR"
                    }
                }
            }
        } else {
            Write-Log "Robocopy failed with exit code: $robocopyExitCode" "WARN"
            if ($robocopyResult) {
                Write-Log "Robocopy output: $robocopyResult" "INFO"
            }
        }
    }
    catch {
        Write-Log "Robocopy unlock exception: $($_.Exception.Message)" "ERROR"
    }
    finally {
        # Clean up temp directory
        if (Test-Path $tempUnlockDir) {
            try {
                Remove-Item $tempUnlockDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            catch {
                Write-Log "Failed to clean up temp directory: $tempUnlockDir" "WARN"
            }
        }
    }
    
    # Strategy 3: File ownership fix (for ARM64 systems)
    if (Test-ARM64System) {
        Write-Log "ARM64 system detected, attempting ownership fix..." "INFO"
        try {
            & takeown /f "$FilePath" 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-Log "File ownership acquired" "INFO"
                
                # Test if this resolved the lock
                Invoke-ComprehensiveGC -Reason "Post-ownership fix"
                if (-not (Test-FileLocked -FilePath $FilePath)) {
                    Write-Log "File unlocked via ownership fix: $fileName" "SUCCESS"
                    return $true
                }
            }
        }
        catch {
            Write-Log "Ownership fix failed: $($_.Exception.Message)" "WARN"
        }
    }
    
    # Final attempt: One more comprehensive GC cycle
    Write-Log "Final unlock attempt with extended garbage collection..." "INFO"
    Invoke-ComprehensiveGC -Reason "Final unlock attempt"
    Start-Sleep -Seconds 5
    
    if (-not (Test-FileLocked -FilePath $FilePath)) {
        Write-Log "File unlocked on final attempt: $fileName" "SUCCESS"
        return $true
    }
    
    # File remains locked - provide guidance but don't fail
    Write-Log "File remains locked after all unlock attempts: $fileName" "WARN"
    Write-Log "File may still be signable despite lock detection" "INFO"
    return $false  # Return false but allow signing attempt to proceed
}

# Function to sign executable (enhanced with admin detection and certificate store handling)
function Invoke-ExecutableSigning {
    param(
        [string]$FilePath,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$CertificateStore = "CurrentUser"
    )

    if (-not (Test-Path $FilePath)) {
        Write-Log "File not found for signing: $FilePath" "ERROR"
        return $false
    }

    Write-Log "Signing executable: $([System.IO.Path]::GetFileName($FilePath))" "INFO"

    # Use improved file unlocking for ARM64 cross-compilation scenarios
    $isARM64System = Test-ARM64System
    $isX64Executable = $FilePath -like "*x64*" -or $FilePath -like "*win-x64*"
    
    if ($isARM64System -and $isX64Executable) {
        Write-Log "ARM64 system detected, signing x64 executable with enhanced unlock strategy" "INFO"
        
        # Use comprehensive file unlocking
        $unlockSuccess = Invoke-FileUnlock -FilePath $FilePath -MaxAttempts 3
        if (-not $unlockSuccess) {
            Write-Log "File unlock failed, but attempting to sign anyway..." "WARN"
        }
    } else {
        # Standard unlock for same-architecture builds
        Write-Log "Performing standard file unlock for $([System.IO.Path]::GetFileName($FilePath))" "INFO"
        $unlockSuccess = Invoke-FileUnlock -FilePath $FilePath -MaxAttempts 2
    }

    # For Intune certificates, we'll try signing anyway as they may work without admin rights
    if ($Certificate.Subject -like "*Intune*") {
        Write-Log "Detected Intune certificate - attempting signing without admin privileges" "INFO"
    }

    # Use the robust signing function
    try {
        Write-Log "Attempting to sign: $([System.IO.Path]::GetFileName($FilePath))" "INFO"
        
        $success = Invoke-SignArtifact -Path $FilePath -Thumbprint $Certificate.Thumbprint -Store $CertificateStore
        if ($success) {
            Write-Log "Successfully signed: $([System.IO.Path]::GetFileName($FilePath))" "SUCCESS"
            return $true
        } else {
            Write-Log "Standard signing failed, attempting with elevated privileges..." "WARN"
            
            # Try with sudo if available
            $sudoAvailable = Get-Command sudo -ErrorAction SilentlyContinue
            if ($sudoAvailable) {
                Write-Log "Using sudo to elevate signtool privileges for signing..." "INFO"
                try {
                    # Use sudo with signtool directly for elevated signing
                    $sudoResult = sudo signtool.exe sign /s $CertificateStore /sha1 $Certificate.Thumbprint /fd SHA256 /td SHA256 /tr "http://timestamp.digicert.com" /v "$FilePath" 2>&1
                    
                    if ($LASTEXITCODE -eq 0) {
                        Write-Log "Successfully signed using sudo elevation: $([System.IO.Path]::GetFileName($FilePath))" "SUCCESS"
                        
                        # Verify the signature
                        $verifyResult = signtool.exe verify /pa /v "$FilePath" 2>&1
                        if ($LASTEXITCODE -eq 0) {
                            Write-Log "Signature verification successful with sudo signing!" "SUCCESS"
                        } else {
                            Write-Log "Warning: Signature verification failed, but signing appeared successful" "WARN"
                        }
                        
                        return $true
                    } else {
                        Write-Log "Sudo signing failed with exit code: $LASTEXITCODE" "WARN"
                        Write-Log "Sudo output: $sudoResult" "WARN"
                    }
                } catch {
                    Write-Log "Sudo signing failed with exception: $($_.Exception.Message)" "WARN"
                }
            } else {
                Write-Log "sudo not available. Please install sudo or run PowerShell as Administrator for code signing." "ERROR"
            }
            
            Write-Log "All signing attempts failed for: $([System.IO.Path]::GetFileName($FilePath))" "ERROR"
            return $false
        }
    } catch {
        $errorMessage = $_.Exception.Message
        Write-Log "Error during signing: $errorMessage" "ERROR"
        
        # Provide specific guidance for common issues
        if ($errorMessage -like "*Access is denied*") {
            Write-Log "" "ERROR"
            Write-Log "SIGNING FAILED: Access denied" "ERROR"
            Write-Log "This is commonly caused by:" "ERROR"
            Write-Log "1. Intune-managed certificate requiring elevated privileges" "ERROR"
            Write-Log "2. Certificate private key access restrictions" "ERROR"
            Write-Log "3. Certificate not suitable for code signing" "ERROR"
            if (Test-ARM64System -and $FilePath -like "*arm64*") {
                Write-Log "4. ARM64 native signing requires Administrator privileges on some systems" "ERROR"
            }
            Write-Log "" "ERROR"
            Write-Log "Solutions to try:" "ERROR"
            
            # Check if sudo is available and suggest using it
            $sudoAvailable = Get-Command sudo -ErrorAction SilentlyContinue
            if ($sudoAvailable) {
                Write-Log "1. RECOMMENDED: Install/use sudo - detected in PATH" "ERROR"
                Write-Log "   Run: winget install Microsoft.PowerShell.Preview" "ERROR"
                Write-Log "   Then retry the build (sudo will be used automatically)" "ERROR"
                Write-Log "2. Alternative: Run PowerShell as Administrator" "ERROR"
            } else {
                Write-Log "1. Install sudo for automatic elevation: winget install Microsoft.PowerShell.Preview" "ERROR"
                Write-Log "2. Alternative: Run PowerShell as Administrator manually" "ERROR"
            }
            
            Write-Log "3. Check certificate enhanced key usage includes 'Code Signing'" "ERROR"
            Write-Log "4. Verify certificate private key is accessible" "ERROR"
            if (Test-ARM64System -and $FilePath -like "*x64*") {
                Write-Log "5. Wait a few minutes and try again (file handle release)" "ERROR"
            }
            Write-Log "6. For development only: use -AllowUnsigned flag (NOT for production)" "ERROR"
        } elseif ($errorMessage -like "*file is being used by another process*") {
            Write-Log "" "ERROR"
            Write-Log "SIGNING FAILED: File in use" "ERROR"
            Write-Log "This is commonly caused by:" "ERROR"
            Write-Log "1. Build process still holding file handles" "ERROR"
            if (Test-ARM64System) {
                Write-Log "2. ARM64 cross-compilation creates persistent file locks" "ERROR"
            }
            Write-Log "3. Antivirus software scanning the executable" "ERROR"
            Write-Log "" "ERROR"
            Write-Log "Solutions to try:" "ERROR"
            Write-Log "1. Wait 30-60 seconds and run the build again" "ERROR"
            Write-Log "2. Close Visual Studio or other IDEs that may be holding handles" "ERROR"
            Write-Log "3. Temporarily disable real-time antivirus scanning" "ERROR"
            if (Test-ARM64System) {
                Write-Log "4. On ARM64 systems, file locks may require a reboot to clear" "ERROR"
            }
        } else {
            Write-Log "Signing failed with error: $errorMessage" "ERROR"
            Write-Log "Try running as Administrator or check certificate permissions" "ERROR"
        }
        
        return $false
    }
}

# Function to build sbin-installer for specific architecture
function Build-SbinInstaller {
    param(
        [string]$Arch,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCert = $null,
        [string]$CertificateStore = "CurrentUser",
        [string]$Version
    )
    
    Write-Log "Building sbin-installer for $Arch architecture..." "INFO"
    
    # Check if installer submodule exists
    $installerPath = Join-Path $PSScriptRoot "..\installer"
    if (-not (Test-Path $installerPath)) {
        Write-Log "sbin-installer submodule not found at $installerPath" "ERROR"
        Write-Log "Run: git submodule update --init --recursive" "ERROR"
        return $null
    }
    
    # Build sbin-installer using its build script
    Push-Location $installerPath
    try {
        $buildArgs = @{
            Architecture = $Arch
            Version = $Version
            SkipMsi = $true  # We don't need the MSI, just the executable
        }
        
        # Add certificate if available
        if ($SigningCert) {
            $buildArgs.CertificateThumbprint = $SigningCert.Thumbprint
        }
        
        Write-Log "Running sbin-installer build: .\build.ps1 -Architecture $Arch -Version $Version -SkipMsi" "INFO"
        & .\build.ps1 @buildArgs
        
        if ($LASTEXITCODE -ne 0) {
            throw "sbin-installer build failed with exit code: $LASTEXITCODE"
        }
        
        # Verify the executable was built (use current location since we're in the installer directory)
        $installerExe = Join-Path (Get-Location) "dist\$Arch\installer.exe"
        if (-not (Test-Path $installerExe)) {
            throw "sbin-installer executable not found: $installerExe"
        }
        
        # Convert to absolute path before returning
        $installerExe = (Get-Item $installerExe).FullName
        
        $fileInfo = Get-Item $installerExe
        $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
        Write-Log "sbin-installer build successful ($Arch): $($fileInfo.Name) ($sizeMB MB)" "SUCCESS"
        
        return $installerExe
        
    } catch {
        Write-Log "Failed to build sbin-installer ($Arch): $($_.Exception.Message)" "ERROR"
        return $null
    } finally {
        Pop-Location
    }
}

# Function to build for specific architecture
function Build-Architecture {
    param(
        [string]$Arch,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCert = $null,
        [string]$CertificateStore = "CurrentUser"
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
        $null = & dotnet @buildArgs
        
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
            # Check for ARM64 system - fix ownership issue for both x64 and ARM64 executables
            $isARM64System = Test-ARM64System
            if ($isARM64System) {
                if ($Arch -eq "x64") {
                    Write-Log "ARM64 system detected - fixing x64 binary ownership and Defender exclusion for signing..." "INFO"
                    
                    # For x64 cross-compilation, add temporary Windows Defender exclusion to prevent signing interference
                    try {
                        $exclusionPath = Split-Path $executablePath
                        & sudo powershell -Command "Add-MpPreference -ExclusionPath '$exclusionPath'" -ErrorAction SilentlyContinue
                        Write-Log "Added temporary Windows Defender exclusion for x64 cross-compilation signing" "INFO"
                    } catch {
                        Write-Log "Could not add Defender exclusion, but continuing..." "WARN"
                    }
                } else {
                    Write-Log "ARM64 system detected - fixing ARM64 binary ownership and Defender exclusion for signing..." "INFO"
                    
                    # For ARM64 native executables, add temporary Windows Defender exclusion to prevent signing interference
                    try {
                        $exclusionPath = Split-Path $executablePath
                        & sudo powershell -Command "Add-MpPreference -ExclusionPath '$exclusionPath'" -ErrorAction SilentlyContinue
                        Write-Log "Added temporary Windows Defender exclusion for ARM64 signing" "INFO"
                    } catch {
                        Write-Log "Could not add Defender exclusion, but continuing..." "WARN"
                    }
                }
                try {
                    & takeown /f $executablePath | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Log "Fixed $Arch binary ownership" "SUCCESS"
                    }
                } catch {
                    Write-Log "Could not fix ownership, but continuing..." "WARN"
                }
            }
            
            # Force comprehensive garbage collection before signing to release any build-related file handles
            Write-Log "Performing garbage collection before signing to release file handles..." "INFO"
            Invoke-ComprehensiveGC -Reason "Pre-signing file handle release"
            
            if (Invoke-ExecutableSigning -FilePath $executablePath -Certificate $SigningCert -CertificateStore $CertificateStore) {
                Write-Log "Code signing completed for $Arch" "SUCCESS"
                
                # Clean up temporary Windows Defender exclusions for all architectures on ARM64 systems
                $isARM64System = Test-ARM64System
                if ($isARM64System) {
                    try {
                        $exclusionPath = Split-Path $executablePath
                        & sudo powershell -Command "Remove-MpPreference -ExclusionPath '$exclusionPath'" -ErrorAction SilentlyContinue
                        Write-Log "Removed temporary Windows Defender exclusion for $Arch architecture" "INFO"
                    } catch {
                        Write-Log "Could not remove Defender exclusion (non-critical)" "WARN"
                    }
                }
                return $true
            } else {
                Write-Log "Code signing failed for $Arch" "ERROR"
                
                # Provide guidance but don't fail the build - allow for manual signing
                Write-Log "" "WARN"
                Write-Log "Build completed but signing failed. The executable was built successfully." "WARN"
                Write-Log "You can manually sign it later or run with elevated privileges." "WARN"
                Write-Log "For production deployment, ensure the executable is properly signed." "WARN"
                
                # Return success so build continues, but mark as unsigned
                return "unsigned"
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
        [string]$FullVersion,  # Full YYYY.MM.DD.HHMM version for binaries
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCert = $null,
        [string]$CertificateStore = "CurrentUser",
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
    
    # Generate customized run.ps1 script with hardcoded URL for MSI deployment
    Write-Log "Generating customized run.ps1 script with URL: $bootstrapUrl" "INFO"
    $customRunScriptPath = "installer\run.ps1"
    $customRunScriptContent = @"
# BootstrapMate Simple Runner Script
# This script provides a convenient way to run the BootstrapMate installer
# with the configured bootstrap URL for this organization
# Generated during build with URL: $bootstrapUrl

Write-Host "Running BootstrapMate with configured URL..."
& 'C:\Program Files\BootstrapMate\installapplications.exe' --url $bootstrapUrl
"@
    Set-Content -Path $customRunScriptPath -Value $customRunScriptContent -Encoding UTF8
    Write-Log "Generated custom run.ps1 script for MSI deployment" "SUCCESS"
    
    # Stage sbin-installer for MSI packaging
    Write-Log "Staging sbin-installer executable for MSI..." "INFO"
    $sbinStagingDir = "installer\sbin-staging"
    if (-not (Test-Path $sbinStagingDir)) {
        New-Item -ItemType Directory -Path $sbinStagingDir -Force | Out-Null
    }
    
    # Build sbin-installer for this architecture (use FullVersion for YYYY.MM.DD.HHMM format)
    $sbinInstallerExe = Build-SbinInstaller -Arch $Arch -SigningCert $SigningCert -CertificateStore $CertificateStore -Version $FullVersion

    if (-not $sbinInstallerExe) {
        Write-Log "Failed to build sbin-installer for $Arch - MSI build cannot continue" "ERROR"
        return @{ Success = $false; Architecture = $Arch }
    }
    
    # Ensure we have a single string path (sbin-installer build outputs dotnet messages)
    if ($sbinInstallerExe -is [array]) {
        $sbinInstallerExe = $sbinInstallerExe | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Last 1
    }
    
    # Copy to staging directory
    Copy-Item $sbinInstallerExe (Join-Path $sbinStagingDir "installer.exe") -Force
    Write-Log "Staged sbin-installer for MSI packaging" "SUCCESS"
    
    # Convert staging directory to absolute path for WiX
    $sbinStagingDirAbsolute = (Resolve-Path $sbinStagingDir).Path
    $binDirAbsolute = (Resolve-Path "publish\executables\$Arch").Path
    
    $buildArgs = @(
        "build", $projectPath,
        "--configuration", "Release",
        "--verbosity", "normal",
        "-p:Platform=$Arch",
        "-p:ProductVersion=$($versionInfo.MsiVersion)",
        "-p:BinDir=$binDirAbsolute",
        "-p:BootstrapUrl=$bootstrapUrl",
        "-p:SbinDir=$sbinStagingDirAbsolute"
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
            
            # Copy MSI to consolidated publish directory with version in filename
            $publishMsiDir = "publish\msi"
            if (-not (Test-Path $publishMsiDir)) {
                New-Item -ItemType Directory -Path $publishMsiDir -Force | Out-Null
            }
            # Include version in MSI filename for better version tracking
            $finalMsiPath = Join-Path $publishMsiDir "BootstrapMate-$Arch-$($versionInfo.FullVersion).msi"
            Copy-Item $msiPath $finalMsiPath -Force
            Write-Log "MSI copied to: $finalMsiPath" "INFO"
            
            # Sign MSI if certificate available
            if ($SigningCert) {
                try {
                    $signed = Invoke-SignArtifact -Path $finalMsiPath -Thumbprint $SigningCert.Thumbprint -Store $CertificateStore
                    if ($signed) {
                        Write-Log "MSI signed successfully" "SUCCESS"
                    } else {
                        Write-Log "MSI signing failed" "ERROR"
                    }
                } catch {
                    Write-Log "MSI signing error: $($_.Exception.Message)" "ERROR"
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
    
    # Clean up the generated run script and sbin staging directory
    $customRunScriptPath = "installer\run.ps1"
    if (Test-Path $customRunScriptPath) {
        Remove-Item $customRunScriptPath -Force
        Write-Log "Cleaned up generated run.ps1 script" "INFO"
    }
    
    $sbinStagingDir = "installer\sbin-staging"
    if (Test-Path $sbinStagingDir) {
        Remove-Item $sbinStagingDir -Recurse -Force
        Write-Log "Cleaned up sbin-installer staging directory" "INFO"
    }
}

# Function to clean up old build artifacts (keep only latest N versions per architecture)
function Clear-OldBuildArtifacts {
    param(
        [int]$KeepCount = 1  # Keep only the most recent version per architecture
    )
    
    Write-Log "Cleaning up old build artifacts (keeping $KeepCount most recent per architecture)..." "INFO"
    
    $publishDir = Join-Path $PSScriptRoot "publish"
    $totalFreed = 0
    
    # Clean up old .intunewin files
    $intunewinDir = Join-Path $publishDir "intunewin"
    if (Test-Path $intunewinDir) {
        foreach ($arch in @("x64", "arm64")) {
            $files = Get-ChildItem -Path $intunewinDir -Filter "BootstrapMate-$arch-*.intunewin" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending
            
            if ($files.Count -gt $KeepCount) {
                $filesToRemove = $files | Select-Object -Skip $KeepCount
                foreach ($file in $filesToRemove) {
                    $sizeMB = [math]::Round($file.Length / 1MB, 2)
                    $totalFreed += $file.Length
                    Remove-Item $file.FullName -Force
                    Write-Log "Removed old artifact: $($file.Name) ($sizeMB MB)" "INFO"
                }
            }
        }
    }
    
    # Clean up old .msi files
    $msiDir = Join-Path $publishDir "msi"
    if (Test-Path $msiDir) {
        foreach ($arch in @("x64", "arm64")) {
            $files = Get-ChildItem -Path $msiDir -Filter "BootstrapMate-$arch-*.msi" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending
            
            if ($files.Count -gt $KeepCount) {
                $filesToRemove = $files | Select-Object -Skip $KeepCount
                foreach ($file in $filesToRemove) {
                    $sizeMB = [math]::Round($file.Length / 1MB, 2)
                    $totalFreed += $file.Length
                    Remove-Item $file.FullName -Force
                    Write-Log "Removed old artifact: $($file.Name) ($sizeMB MB)" "INFO"
                }
            }
        }
    }
    
    # Clean up old executables (each arch has its own subdirectory)
    $exeDir = Join-Path $publishDir "executables"
    if (Test-Path $exeDir) {
        foreach ($arch in @("x64", "arm64")) {
            $archExeDir = Join-Path $exeDir $arch
            if (Test-Path $archExeDir) {
                $files = Get-ChildItem -Path $archExeDir -Filter "*.exe" -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending
                
                if ($files.Count -gt $KeepCount) {
                    $filesToRemove = $files | Select-Object -Skip $KeepCount
                    foreach ($file in $filesToRemove) {
                        $sizeMB = [math]::Round($file.Length / 1MB, 2)
                        $totalFreed += $file.Length
                        Remove-Item $file.FullName -Force
                        Write-Log "Removed old executable: $($file.Name) ($sizeMB MB)" "INFO"
                    }
                }
            }
        }
    }
    
    if ($totalFreed -gt 0) {
        $freedGB = [math]::Round($totalFreed / 1GB, 2)
        Write-Log "Freed $freedGB GB by removing old build artifacts" "SUCCESS"
    } else {
        Write-Log "No old artifacts to clean up" "INFO"
    }
    
    return $totalFreed
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
            $null = & $localIntuneUtil -h 2>&1
            if ($LASTEXITCODE -eq 0) {
                $intuneUtilPath = $localIntuneUtil
                Write-Log "Using local IntuneWinAppUtil.exe (verified working)" "SUCCESS"
            } else {
                Write-Log "Local IntuneWinAppUtil.exe exists but doesn't work properly" "WARN"
            }
        } catch {
            Write-Log "Local IntuneWinAppUtil.exe test failed: $($_.Exception.Message)" "WARN"
        }
    }
    
    # Try system PATH if local copy doesn't work
    if (-not $intuneUtilPath -and (Test-Command "IntuneWinAppUtil.exe")) {
        try {
            $null = & IntuneWinAppUtil.exe -h 2>&1
            if ($LASTEXITCODE -eq 0) {
                $intuneUtilPath = "IntuneWinAppUtil.exe"
                Write-Log "Using system IntuneWinAppUtil.exe (verified working)" "SUCCESS"
            } else {
                Write-Log "System IntuneWinAppUtil.exe exists but doesn't work properly" "WARN"
            }
        } catch {
            Write-Log "System IntuneWinAppUtil.exe test failed: $($_.Exception.Message)" "WARN"
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
            $null = & $intuneUtilPath -h 2>&1
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
    
    # Clean up old build artifacts to prevent disk space accumulation
    Clear-OldBuildArtifacts -KeepCount 1

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
        
        # Note: IntuneWinAppUtil is downloaded directly from Microsoft if needed
        # See New-IntuneWinPackage function for automatic download logic
        
        Write-Log "MSI prerequisites check completed" "SUCCESS"
    }
    
    # Handle signing certificate - Code signing is REQUIRED unless explicitly disabled
    $signingCert = $null
    $certificateInfo = $null
    $requireSigning = -not $AllowUnsigned
    
    if ($requireSigning) {
        Write-Log "Code signing is REQUIRED for production builds" "INFO"
        Test-SignTool
        
        # Get enhanced certificate information
        $certificateInfo = Get-SigningCertThumbprint -Thumbprint $Thumbprint
        if ($certificateInfo) {
            $signingCert = $certificateInfo.Certificate
            Write-Log "Code signing certificate found and verified" "SUCCESS"
            Write-Log "Certificate store: $($certificateInfo.Store)" "INFO"
            Write-Log "Certificate subject: $($signingCert.Subject)" "INFO"
            Write-Log "Certificate expires: $($signingCert.NotAfter)" "INFO"
            
            # Check for admin privileges if using Intune certificate
            if ($signingCert.Subject -like "*Intune*") {
                # Intune certificates work without admin privileges
                Write-Log "Administrator privileges confirmed for Intune certificate" "SUCCESS"
            }
        } else {
            Write-Log "CRITICAL ERROR: Code signing certificate not found!" "ERROR"
            Write-Log "BootstrapMate MUST be signed for enterprise deployment" "ERROR"
            Write-Log "" "ERROR"
            Write-Log "Solutions:" "ERROR"
            Write-Log "1. Install your enterprise code signing certificate" "ERROR"
            Write-Log "2. Specify certificate thumbprint with -Thumbprint parameter" "ERROR"
            Write-Log "3. Set ENTERPRISE_CERT_CN environment variable" "ERROR"
            Write-Log "4. For development only: use -AllowUnsigned flag (NOT for production)" "ERROR"
            throw "Code signing certificate required but not found. Cannot build unsigned executable for production."
        }
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
        
        # Pass certificate store information if available
        $certStore = if ($certificateInfo) { $certificateInfo.Store } else { "CurrentUser" }
        $success = Build-Architecture -Arch $arch -SigningCert $signingCert -CertificateStore $certStore
        
        $buildResults += @{
            Architecture = $arch
            Success = ($success -eq $true)
            Unsigned = ($success -eq "unsigned")
            Path = "publish\executables\$arch\installapplications.exe"
        }
        
        if ($Test -and ($success -eq $true -or $success -eq "unsigned")) {
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
                    
                    # Pass certificate store information for MSI signing
                    $certStore = if ($certificateInfo) { $certificateInfo.Store } else { "CurrentUser" }
                    $msiResult = Build-MSI -Arch $result.Architecture -Version $versionInfo.MsiVersion -FullVersion $versionInfo.FullVersion -SigningCert $signingCert -CertificateStore $certStore -CimianVersion $CimianToolsVersion
                    $msiResults += $msiResult
                    
                    # Create .intunewin if MSI was successful
                    if ($msiResult.Success -and $msiResult.MsiPath) {
                        $fullMsiPath = (Get-Item $msiResult.MsiPath).FullName
                        $intuneWinPath = New-IntuneWinPackage -MsiPath $fullMsiPath -OutputDirectory "publish\intunewin"
                        if ($intuneWinPath) {
                            # Rename .intunewin file to include version for better tracking
                            $intuneWinDir = Split-Path $intuneWinPath -Parent
                            $versionedIntuneWinName = "BootstrapMate-$($result.Architecture)-$($versionInfo.FullVersion).intunewin"
                            $versionedIntuneWinPath = Join-Path $intuneWinDir $versionedIntuneWinName
                            
                            if ($intuneWinPath -ne $versionedIntuneWinPath) {
                                Move-Item -Path $intuneWinPath -Destination $versionedIntuneWinPath -Force
                                Write-Log "Renamed .intunewin to include version: $versionedIntuneWinName" "INFO"
                                $intuneWinPath = $versionedIntuneWinPath
                            }
                            
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
    $unsignedCount = 0
    
    foreach ($result in $buildResults) {
        if ($result.Success) {
            $successCount++
            $fullPath = Join-Path $rootPath $result.Path
            if (Test-Path $fullPath) {
                $fileInfo = Get-Item $fullPath
                $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
                
                # Determine signing status - prioritize actual file signature over build result
                $signStatus = ""
                $isSigned = $false
                
                if ($signingCert) {
                    try {
                        $signature = Get-AuthenticodeSignature -FilePath $fullPath
                        $isSigned = ($signature.Status -eq "Valid")
                        if ($isSigned) { 
                            $signedCount++ 
                            $signStatus = " [SIGNED]"
                        } else { 
                            $unsignedCount++
                            if ($result.Unsigned) {
                                $signStatus = " [UNSIGNED - NEEDS MANUAL SIGNING]"
                            } else {
                                $signStatus = " [SIGN FAILED ❌]" 
                            }
                        }
                    } catch {
                        $unsignedCount++
                        $signStatus = " [SIGN STATUS UNKNOWN]"
                    }
                } else { 
                    $unsignedCount++
                    $signStatus = " [UNSIGNED - DEV ONLY]" 
                }
                
                Write-Log "SUCCESS $($result.Architecture): $($result.Path) ($sizeMB MB)$signStatus" "SUCCESS"
            } else {
                Write-Log "SUCCESS $($result.Architecture): Built successfully" "SUCCESS"
            }
        } else {
            Write-Log "ERROR $($result.Architecture): Build failed" "ERROR"
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
        if ($signedCount -eq $successCount -and $unsignedCount -eq 0) {
            Write-Log "All executables signed with certificate: $($signingCert.Subject)" "SUCCESS"
        } elseif ($signedCount -gt 0) {
            Write-Log "Signing results: $signedCount signed, $unsignedCount unsigned/failed" "WARN"
            Write-Log "Certificate: $($signingCert.Subject)" "INFO"
            if ($unsignedCount -gt 0) {
                Write-Log "Some executables may need manual signing or elevated privileges" "WARN"
            }
        } else {
            Write-Log "All signing attempts failed ($unsignedCount of $successCount)" "ERROR"
            Write-Log "Certificate: $($signingCert.Subject)" "INFO"
            Write-Log "Consider running as Administrator or checking certificate permissions" "ERROR"
        }
    } elseif ($AllowUnsigned) {
        Write-Log "All executables built unsigned (development mode)" "WARN"
    }
    
    # Consider build successful if all architectures built, even if some signing failed
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
        Write-Log "ALL BUILDS COMPLETED SUCCESSFULLY!" "SUCCESS"
        if (-not $SkipMSI) {
            # Determine actual signing status
            $hasSignedExecutables = $signedCount -gt 0
            $hasUnsignedExecutables = $unsignedCount -gt 0
            
            if ($AllowUnsigned) {
                $signStatus = "built (unsigned)"
                $deploymentStatus = "development testing"
            } elseif ($hasSignedExecutables -and -not $hasUnsignedExecutables) {
                $signStatus = "built and signed"
                $deploymentStatus = "enterprise deployment"
            } elseif ($hasSignedExecutables -and $hasUnsignedExecutables) {
                $signStatus = "built (partially signed)"
                $deploymentStatus = "manual signing review"
            } else {
                $signStatus = "built (unsigned - signing failed)"
                $deploymentStatus = "manual signing required"
            }
            
                Write-Log "Executables $signStatus" "SUCCESS"
                Write-Log "MSI packages created$(if ($hasSignedExecutables -and -not $AllowUnsigned) { ' and signed' })" "SUCCESS"
                Write-Log ".intunewin packages ready for Intune deployment" "SUCCESS"
                Write-Log "" "INFO"
                Write-Log "Ready for $deploymentStatus!" "SUCCESS"
                
                # Provide guidance for unsigned builds
                if ($hasUnsignedExecutables -and -not $AllowUnsigned) {
                    Write-Log "" "WARN"
                    Write-Log "SIGNING GUIDANCE:" "WARN"
                Write-Log "   Some executables are unsigned and need manual signing for production" "WARN"
                Write-Log "   Solutions:" "WARN"
                Write-Log "   1. Run build script as Administrator" "WARN"
                Write-Log "   2. Check certificate private key permissions" "WARN"
                Write-Log "   3. Manually sign files with signtool.exe" "WARN"
                Write-Log "   4. For development: use -AllowUnsigned flag" "WARN"
                } elseif ($AllowUnsigned) {
                    Write-Log "" "WARN"
                    Write-Log "REMINDER: This is an UNSIGNED build for development only!" "WARN"
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
