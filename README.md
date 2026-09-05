# BootstrapMate for Windows

A bootstrapping tool for Windows device provisioning that downloads and installs packages during Windows Out of Box Experience (OOBE) Enrollment Status Page (ESP) or after user login.

## Features

- **Dual Phase Support**: Setup Assistant (pre-login/ESP) and Userland (post-login)
- **Package Types**: MSI, EXE, PowerShell scripts, Chocolatey packages (.nupkg), sbin-installer packages (.pkg)
- **Primary Package Manager**: sbin-installer (lightweight, fast, no cache management) with Chocolatey fallback
- **Registry Status Tracking**: Provides completion status for Intune detection scripts
- **Architecture Support**: x64 and ARM64 with conditional installation
- **Admin Escalation**: Automatic privilege elevation for packages requiring admin rights

## Configuration

Before building, set up your environment variables:

1. **Copy the example environment file**:
   ```powershell
   Copy-Item .env.example .env
   ```

2. **Edit `.env` with your organization's settings**:
   ```bash
   # Your code signing certificate Common Name
   ENTERPRISE_CERT_CN=Your Organization Code Signing Certificate
   
   # Your bootstrap manifest URL
   BOOTSTRAP_MANIFEST_URL=https://example.com/bootstrap/management.json
   
   # Optional: Specific certificate thumbprint
   # CERT_THUMBPRINT=1234567890ABCDEF1234567890ABCDEF12345678
   ```

3. **Install your code signing certificate** in the Current User certificate store

## Reporting

When a run completes, BootstrapMate can POST a vendor-neutral JSON run summary to an optional endpoint, turning "did this PC provision cleanly?" into a fleet-dashboard query. The payload is plain JSON and not tied to any specific backend — any service accepting a JSON POST (a custom collector, ReportMate, MunkiReport, etc.) can consume it. Both the Windows and macOS clients emit the same schema.

Configure via Intune CSP / Group Policy (the bundled ADMX), the machine/user registry, or both keys below:

| Key | Type | Effect |
|---|---|---|
| `ReportingUrl` | string | Endpoint to POST the run summary to. When unset, no report is sent. |
| `ReportingHeader` | string | Optional `Authorization` header value sent with the POST. |

The POST is best-effort: it is bounded by a short timeout and never fails the run (a slow or unreachable endpoint can delay completion by up to that timeout). Payload fields include `tool`, `platform`, `version`, `runId`, `success`, `startTime`/`endTime`, `durationSeconds`, `architecture`, `hostname`, `serialNumber`, `manifestUrl`, and per-phase outcomes.
## Security: package signature verification

Before any MSI or EXE installer is executed elevated, BootstrapMate verifies its Authenticode signature with `WinVerifyTrust`. A successful download only proves where the bytes came from — not who produced them. The signature gate ensures an installer carries a signature that chains to a trusted root (and, when configured, matches an expected publisher) before it runs as an elevated process.

Behaviour is controlled via Intune CSP / Group Policy (the bundled ADMX), the machine/user registry, or per-item manifest fields.

Policy / registry keys (`HKLM\SOFTWARE\Policies\BootstrapMate` for policy; `HKLM\SOFTWARE\BootstrapMate\Settings` for machine settings):

| Key | Type | Default | Effect |
|---|---|---|---|
| `VerifyPackageSignatures` | DWORD | `1` | Verify every MSI/EXE installer before running it. |
| `ExpectedPublisher` | string | _unset_ | Require installers to be signed by a certificate whose common name/subject contains this value. When unset, any Windows-trusted signature is accepted. |
| `AllowUnsigned` | DWORD | `0` | Permit unsigned/untrusted installers (logged as a warning). A publisher *mismatch* is never bypassed, even with this set. |

Per-item manifest overrides (fall back to the global config): `expectedPublisher`, `allowUnsigned`.

Only `msi` and `exe` items are Authenticode-gated; `nupkg`/`pkg`/`ps1` items continue to rely on their existing handling.

## Quick Start

```powershell
# Build signed executables + MSI + .intunewin (production)
.\build.ps1

# Development build (unsigned - for testing only)
.\build.ps1 -AllowUnsigned

# Build specific architecture
.\build.ps1 -Architecture x64

# Build without MSI/IntuneWin packages
.\build.ps1 -SkipMSI

# Run with a manifest URL
.\publish\executables\x64\managedbootstrapinstall.exe --url "https://example.com/bootstrap/management.json"

# Check status (useful for troubleshooting)
.\publish\executables\x64\managedbootstrapinstall.exe --status

# Clear status (for testing)
.\publish\executables\x64\managedbootstrapinstall.exe --clear-status
```

## Registry Status Contract

BootstrapMate tracks completion status in both 64-bit and 32-bit registry views:

```
HKLM\SOFTWARE\BootstrapMate\LastRunVersion                    # Written only after successful completion
HKLM\SOFTWARE\BootstrapMate\Status\SetupAssistant
HKLM\SOFTWARE\BootstrapMate\Status\Userland
HKLM\SOFTWARE\WOW6432Node\BootstrapMate\Status\SetupAssistant  
HKLM\SOFTWARE\WOW6432Node\BootstrapMate\Status\Userland
```

**Status Values**: `Starting`, `Running`, `Completed`, `Failed`, `Skipped`

**Completion Registry Value** (written only after successful run):
- `LastRunVersion`: BootstrapMate version that successfully completed (e.g., "2025.08.30.1300")

**For Intune Detection**: Use `HKLM\SOFTWARE\BootstrapMate\LastRunVersion` as your detection key.

## Intune Implementation

### Option 1: MSI Deployment (Recommended)

The most reliable way to deploy BootstrapMate is using the signed MSI installer:

```powershell
# Build signed executables, MSI and .intunewin packages with an auto-detected certificate
.\build.ps1

# Deploy via Intune Win32 app using generated files:
# - BootstrapMate-x64-VERSION.msi (signed, for x64 systems)
# - BootstrapMate-arm64-VERSION.msi (signed, for ARM64 systems)  
# - install-bootstrapmate.ps1 (installation script)
# - detect-bootstrapmate.ps1 (detection script)
# - BootstrapMate-x64-VERSION.intunewin (for direct upload)
# - BootstrapMate-arm64-VERSION.intunewin (for direct upload)
```

**Benefits of MSI deployment:**
- ✅ Proper Windows Installer integration
- ✅ **Code signed with enterprise certificate**
- ✅ Automatic architecture detection
- ✅ Clean uninstall capability
- ✅ Shows in Add/Remove Programs
- ✅ Reliable upgrade path
- ✅ **.intunewin packages for direct Intune upload**

### Option 2: PowerShell Script Deployment

For simple deployments, you can package the executable with a PowerShell script:

### Detection Script for Intune Win32 App

Use this PowerShell detection script in your Intune Win32 app configuration:

```powershell
# Intune Detection Script for BootstrapMate
$regPath = "HKLM:\SOFTWARE\BootstrapMate"
$expectedVersion = "2025.08.30.1300"  # Update this when you deploy new versions

try {
    $lastRunVersion = Get-ItemProperty -Path $regPath -Name "LastRunVersion" -ErrorAction Stop
    if ($lastRunVersion.LastRunVersion -eq $expectedVersion) {
        Write-Output "BootstrapMate $expectedVersion completed successfully"
        exit 0  # Found - app is installed
    } else {
        Write-Output "Found version $($lastRunVersion.LastRunVersion), expected $expectedVersion"
        exit 1  # Wrong version - trigger reinstall
    }
} catch {
    Write-Output "BootstrapMate not found or never completed successfully"
    exit 1  # Not found - trigger install
}
```

### Intune Win32 App Configuration

#### Basic Information
- **Name**: BootstrapMate OOBE Bootstrap
- **Description**: Automated software provisioning during Windows OOBE
- **Publisher**: Your Organization
- **Category**: Computer Management

#### Program Settings
- **Install command**: `powershell.exe -ExecutionPolicy Bypass -File install.ps1`
- **Uninstall command**: `powershell.exe -ExecutionPolicy Bypass -Command "Remove-Item -Path 'HKLM:\SOFTWARE\BootstrapMate' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -Path '$env:ProgramFiles\BootstrapMate' -Recurse -Force -ErrorAction SilentlyContinue"`
- **Install behavior**: System
- **Device restart behavior**: No specific action

#### Requirements
- **Operating system architecture**: 64-bit (or configure separate packages for x64/ARM64)
- **Minimum operating system**: Windows 10 1903
- **Disk space required**: 100 MB
- **Physical memory required**: 512 MB

#### Detection Rules
- **Rules format**: Use custom detection script
- **Script file**: Upload the detection script from above

#### Dependencies
- None (BootstrapMate is self-contained)

### Package Structure

Create your Win32 app package with these files:

```
BootstrapMate-Package/
├── managedbootstrapinstall.exe     # BootstrapMate executable (x64 or ARM64)
├── install.ps1                     # Installation script
└── detection.ps1                   # Detection script (see examples/detection-scripts/)
```

### Deployment Strategy

#### Autopilot Deployment
1. **Create Win32 App**: Package BootstrapMate as described above
2. **Assign to Device Groups**: Target your Autopilot device groups
3. **Set as Required**: Deploy as required during ESP
4. **Configure Dependencies**: Ensure this runs before other software

#### Group Assignments
- **Target**: Device groups (Autopilot devices)
- **Assignment type**: Required
- **Delivery optimization**: Download content in background using HTTP only

#### ESP Configuration
In your Autopilot profile ESP settings:
- **Show app installation progress**: Yes
- **Block device use until required apps install**: Yes
- **Include BootstrapMate in required apps list**

## Troubleshooting

### Registry Diagnostic Keys

BootstrapMate creates additional registry keys for troubleshooting:

```
HKLM\SOFTWARE\BootstrapMate\
├── LastRunVersion              # Only exists after successful completion
├── BootstrapStatus            # InstallationStarted, Success, Failed, Error, ArchitectureMismatch
├── InstallationStarted        # Timestamp when installation began
├── CompletionTime            # Timestamp when bootstrap completed
├── LastError                 # Error message if failed
├── ErrorTime                 # Timestamp of last error
├── InstallPath               # Where BootstrapMate was installed
├── PackageArchitecture       # Architecture of deployed package (x64/ARM64)
├── SystemArchitecture        # Detected system architecture code
└── ProcessorName             # Processor name for diagnostics
```

### Log Files

BootstrapMate creates detailed logs:
- **Location**: `C:\ProgramData\ManagedBootstrap\logs\`
- **File name**: one file per run, `YYYY-MM-DD-HHmmss.log`
- **Line format**: `[yyyy-MM-dd HH:mm:ss] LEVEL message` in local time, where `LEVEL` is `DEBUG`, `INFO`, `WARN` or `ERROR` padded to five characters
- **Retention**: files older than 30 days are deleted at the start of each run

### Common Issues

1. **Architecture Mismatch**: Deploy separate packages for x64 and ARM64
2. **Certificate Issues**: Ensure your code signing certificate is deployed via Intune
3. **Network Connectivity**: Manifest URL must be accessible during ESP
4. **Permission Issues**: BootstrapMate automatically elevates to administrator
5. **sbin-installer Not Found**: Deploy sbin-installer first if using .nupkg/.pkg packages for optimal performance

### sbin-installer Troubleshooting

**Check Installation:**
```powershell
# Verify sbin-installer is available
if (Test-Path "C:\Program Files\sbin\installer.exe") {
    Write-Host "sbin-installer is installed"
    & "C:\Program Files\sbin\installer.exe" --vers
} else {
    Write-Host "sbin-installer not found - will use Chocolatey fallback"
}
```

**Common sbin-installer Issues:**
- **Package Format**: Ensure .nupkg/.pkg files are valid ZIP archives
- **Permissions**: Verify BootstrapMate runs as administrator
- **Target Path**: Check target path permissions for installation

### Status Checking

Use this PowerShell command to check BootstrapMate status on a device:

```powershell
# Check BootstrapMate status
$regPath = "HKLM:\SOFTWARE\BootstrapMate"
if (Test-Path $regPath) {
    Get-ItemProperty -Path $regPath | Format-List
} else {
    Write-Host "BootstrapMate registry not found - never installed or completed"
}

# Check detailed status
& "$env:ProgramFiles\BootstrapMate\managedbootstrapinstall.exe" --status
```

### Version Management

#### Updating BootstrapMate
1. **Build new version** — the version is the build timestamp, generated automatically
2. **Update detection script** with new version number
3. **Create new Win32 app** or update existing with supersedence
4. **Deploy to test group** first
5. **Monitor deployment** using Intune reporting
6. **Roll out** to production groups

#### Version Numbering
BootstrapMate uses format: `YYYY.MM.DD.HHMM`
- Example: `2025.08.30.1300` (August 30, 2025, 1:00 PM)

## Overview

BootstrapMate for Windows enables IT administrators to:

- **Bootstrap software deployment** during Windows Setup Assistant (OOBE)
- **Orchestrate package installation** from any web-accessible repository
- **Support multiple package formats** (MSI, EXE, PowerShell, Chocolatey, sbin-installer, MSIX)
- **Work with any MDM solution** (Intune, JAMF Pro, Workspace ONE, etc.)
- **Provide real-time feedback** to users and administrators
- **Handle dependencies and ordering** automatically
- **Leverage sbin-installer** for fast, lightweight package management

## How It Works

### Windows OOBE/Autopilot Workflow

1. **MDM Trigger**: MDM system deploys BootstrapMate via Win32 app or script
2. **First Run**: the MSI runs `managedbootstrapinstall.exe` at `InstallFinalize`
3. **Configuration Download**: Downloads package manifest from configured repository
4. **OOBE Package Installation**: Installs system-level packages during device setup
5. **User Session Packages**: Installs the userland packages once a user session exists
6. **Exit**: the process exits, having written its status to the registry
7. **Self-Heal**: a daily scheduled task (`BootstrapMate Self-Heal`, 03:00 as SYSTEM) re-runs the CLI so a device that missed or failed a phase converges

BootstrapMate is a one-shot process, not a resident service. Nothing supervises it between runs: a failed run is retried by the scheduled task, or by the MDM re-running the app, not by a service restart.

### Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   MDM System    │───►│ managedbootstrap │───►│ Package Repo    │
│ (Intune, etc.)  │    │ install.exe (CLI)│    │ (HTTPS/Azure)   │
└─────────────────┘    └──────────────────┘    └─────────────────┘
                                │
                                ▼
                       ┌──────────────────┐
                       │ Package Manifest │
                       │ (JSON/YAML)      │
                       └──────────────────┘
                                │
                                ▼
                       ┌──────────────────┐
                       │ Software Packages│
                       │ MSI/EXE/PS1/MSIX │
                       └──────────────────┘
```

## Quick Start

### 1. Deploy via MDM (Intune Example)

```powershell
# Deploy as Win32 app or PowerShell script
$installCommand = "managedbootstrapinstall.exe --url https://example.com/bootstrap/bootstrapmate.json --silent"
```

### 2. Package Manifest Structure

```json
{
  "setupassistant": [
    {
      "name": "Microsoft Teams",
      "file": "teams.msi",
      "type": "msi",
      "url": "https://example.com/packages/teams.msi",
      "arguments": ["/quiet", "ALLUSERS=1"]
    },
    {
      "name": "System Utility",
      "file": "system-utility-1.0.0.nupkg",
      "type": "nupkg",
      "url": "https://example.com/packages/system-utility-1.0.0.nupkg",
      "arguments": ["--verbose"]
    }
  ],
  "userland": [
    {
      "name": "Adobe Reader",
      "file": "reader.exe", 
      "type": "exe",
      "url": "https://example.com/packages/reader.exe",
      "arguments": ["/S"]
    },
    {
      "name": "User App",
      "file": "userapp-2.0.0.pkg",
      "type": "pkg",
      "url": "https://example.com/packages/userapp-2.0.0.pkg",
      "target": "CurrentUserHomeDirectory",
      "arguments": ["--verbose"]
    }
  ]
}
```

*Note: For .nupkg and .pkg packages, `target` defaults to `"/"` (system root) when omitted.*

### 3. Supported Package Types

- **MSI**: Windows Installer packages
- **EXE**: Executable installers
- **PowerShell**: `.ps1` scripts with elevation
- **nupkg**: NuGet packages via sbin-installer (primary) or Chocolatey (fallback)
- **pkg**: sbin-installer native packages (lightweight, fast, no cache)
- **MSIX**: Modern Windows packages
- **Registry**: Registry modifications
- **File Copy**: Direct file deployment

#### Package Manager Priority

For `.nupkg` packages:
1. **sbin-installer**: Primary choice (if available at `C:\Program Files\sbin\installer.exe`)
2. **Chocolatey**: Fallback option (automatically installs if needed)

For `.pkg` packages:
1. **sbin-installer**: Native format (requires sbin-installer to be installed)

## sbin-installer Integration

BootstrapMate includes out of the box support for [sbin-installer](https://github.com/windowsadmins/sbin-installer), a lightweight alternative to `choco`.

### Why sbin-installer?

**Advantages over Chocolatey:**
- **2-4x faster** package installations
- **No cache management** - direct package execution  
- **90% less disk usage** - no persistent cache
- **Simple command structure** - `installer --pkg <path> --target <target>`
- **Deterministic behavior** - predictable, reliable operation

### Deployment Options

Deploy sbin-installer before using .nupkg/.pkg packages:

```powershell
# Option 1: MSI Installation (Recommended)
Invoke-WebRequest -Uri "https://github.com/windowsadmins/sbin-installer/releases/latest/download/sbin-installer.msi" -OutFile "sbin-installer.msi"
Start-Process msiexec -ArgumentList "/i sbin-installer.msi /quiet" -Wait

# Option 2: Include in BootstrapMate manifest as first package
{
  "setupassistant": [
    {
      "name": "sbin-installer",
      "file": "sbin-installer.msi", 
      "type": "msi",
      "url": "https://example.com/packages/sbin-installer.msi",
      "arguments": ["/quiet"]
    }
  ]
}
```

### Package Configuration

```json
{
  "setupassistant": [
    {
      "name": "System Tool",
      "file": "systemtool-1.0.0.nupkg",
      "type": "nupkg",
      "url": "https://example.com/packages/systemtool-1.0.0.nupkg",
      "arguments": ["--verbose"]
    }
  ]
}
```

**Target Options** (optional):
- **Omitted** → `"/"` (system root) - Default
- `"CurrentUserHomeDirectory"` → User's home folder  
- `"C:\\Custom\\Path"` → Custom installation path

## Features

### Core Functionality
- One-shot CLI, run by the MSI at install time and by a daily self-heal scheduled task
- OOBE/Autopilot integration
- Multiple package format support
- Dependency resolution
- Progress reporting
- Error handling and retry logic
- Cleanup and self-removal

### Planned Features
- GUI progress window
- Advanced logging and telemetry
- Payload hash verification (Authenticode signature verification is already implemented)
- Rollback capabilities
- Configuration profiles
- Integration with popular MDM systems

## Installation

### Prerequisites
- Windows 10/11 (1809 or later)
- No runtime prerequisite — the executable is published self-contained
- Administrative privileges

### Command Line Options

```powershell
managedbootstrapinstall.exe [OPTIONS]

Options:
  --url <url>               URL of the bootstrapmate.json / .yaml manifest
  --force                   Deprecated; downloads are always fresh
  --verbose, -v             Enable detailed logging
  --silent                  Run with no console output
  --no-dialog               Disable the progress dialog
  --blur-screen             Show the progress dialog full screen
  --dialog-title <text>     Custom progress dialog title
  --dialog-message <text>   Custom progress dialog message
  --pipe <name>             Named pipe for GUI output streaming
  --save-settings           Save GUI settings to the registry
  --status                  Show current installation status
  --clear-status            Clear all installation status data
  --clear-cache             Clear caches, including failed installation files
  --reset-chocolatey        Complete Chocolatey reset
  --version, -V             Print the version and exit
  --help, -h                Show help information
```

## Configuration

### Repository Structure
```
repository/
├── manifest.json          # Package definitions
├── packages/              # Package files
│   ├── teams.msi
│   ├── reader.exe
│   └── scripts/
│       └── setup.ps1
└── config/                # Configuration files
    └── settings.json
```

### Manifest Schema

Items live under the two phase keys, `setupassistant` and `userland`. Each item must
carry `name`, `url`, `file` and `type`; the rest are optional. See
[examples/bootstrapmate.json](examples/bootstrapmate.json) for a runnable copy.

```json
{
  "setupassistant": [
    {
      "name": "Sample Application",
      "file": "SampleApp.msi",
      "url": "https://example.com/bootstrap/packages/SampleApp.msi",
      "type": "msi",
      "arguments": ["/quiet", "/norestart"],
      "condition": "architecture_x64",
      "expectedPublisher": "Example Publisher",
      "allowUnsigned": false
    }
  ],
  "userland": [
    {
      "name": "Modern App",
      "file": "ModernApp-2.0.0.pkg",
      "url": "https://example.com/bootstrap/packages/ModernApp-2.0.0.pkg",
      "type": "pkg",
      "target": "CurrentUserHomeDirectory"
    }
  ]
}
```

`type` is one of `msi`, `exe`, `ps1`, `nupkg` or `pkg`. `condition` accepts
`architecture_x64` or `architecture_arm64`. There is no `hash` key: payload hash
verification is not implemented (tracked in issue #33). YAML manifests are accepted and
converted to the same shape.

## Development

### Building from Source

```powershell
# Clone repository
git clone https://github.com/bootstrapmate/bootstrapmate-windows.git
cd bootstrapmate-windows

# Signed build (signing is the default; a certificate is auto-detected)
.\build.ps1

# Build specific architecture
.\build.ps1 -Architecture x64

# Build and test
.\build.ps1 -Test
```

### Build Script Options

- `-Architecture`: Target architecture (x64, arm64, both)
- `-Thumbprint`: Specific certificate thumbprint to use
- `-Clean`: Clean build directories before building
- `-Test`: Run basic functionality tests after building
- `-AllowUnsigned`: Development build without signing (not for production)
- `-SkipMSI`: Build executables only, skipping MSI and .intunewin
- `-ListCerts` / `-FindCertSubject`: Inspect available code signing certificates

## Security Considerations

1. **Code Signing**: Always sign BootstrapMate executable with your enterprise certificate
2. **HTTPS**: Use HTTPS for all manifest and package URLs
3. **Certificate Deployment**: Deploy your code signing certificate via Intune before BootstrapMate
4. **Manifest Security**: Protect your bootstrap manifest URL from unauthorized access
5. **Package Integrity**: Consider implementing hash verification for downloaded packages

## Best Practices

1. **Test Architecture Combinations**: Test on both x64 and ARM64 devices
2. **Monitor Deployments**: Use Intune device compliance and app installation reports
3. **Staged Rollout**: Deploy to pilot groups before full production
4. **Backup Strategy**: Maintain previous working versions for rollback
5. **Documentation**: Document your manifest structure and package dependencies
6. **Regular Updates**: Keep BootstrapMate updated for security and functionality improvements

### Project Structure

Three projects. There is no Windows Service, and no test project yet:

```
BootstrapMate.csproj        # CLI (managedbootstrapinstall.exe), sources at the repo root
src/BootstrapMate.Core/     # Shared library (constants, signature verification, reporting)
src/BootstrapMate.App/      # WinUI 3 GUI (BootstrapMate.exe), launches the CLI elevated
installer/                  # WiX MSI: runs the CLI at InstallFinalize, registers the self-heal task
examples/                   # Example manifest and Intune detection scripts
```

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Original [InstallApplications](https://github.com/macadmins/installapplications) macOS project
- [sbin-installer](https://github.com/windowsadmins/sbin-installer) for lightweight package management
- [BootstrapMate for Mac](https://github.com/bootstrapmate/bootstrapmate-macintosh), the macOS counterpart
- Windows Admin community for feedback and testing

## Support

- 📚 [Documentation](https://github.com/bootstrapmate/bootstrapmate-windows/wiki)
- 🐛 [Issue Tracker](https://github.com/bootstrapmate/bootstrapmate-windows/issues)
- 💬 [Discussions](https://github.com/bootstrapmate/bootstrapmate-windows/discussions)
- 📖 [Examples](https://github.com/bootstrapmate/bootstrapmate-windows/tree/main/examples)
