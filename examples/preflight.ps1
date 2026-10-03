<#
.SYNOPSIS
    Example BootstrapMate preflight script.

.DESCRIPTION
    Runs before any package is installed and chooses what the rest of the run does:
      0 = Skip      (nothing to do on this machine)
      2 = Baseline  (already provisioned: refresh setupassistant items without a
                     dialog and skip userland)
      1 = Provision (full bootstrap)

    An older BootstrapMate without baseline mode reads 2 as Provision, so only ask
    for baseline when BOOTSTRAPMATE_BASELINE_EXIT_CODE says this build supports it.
#>

$provisioned = Test-Path 'HKLM:\SOFTWARE\Example\Provisioned'

if (-not $provisioned) {
    exit 1
}

if ($env:BOOTSTRAPMATE_BASELINE_EXIT_CODE) {
    exit [int]$env:BOOTSTRAPMATE_BASELINE_EXIT_CODE
}

exit 0
