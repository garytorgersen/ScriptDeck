# Sample script for ScriptDeck. Demonstrates structured PSObject output
# (lands in the grid) plus an Information stream message (cyan in the
# console RTB). Pass a hostname or "." for the local box.
[CmdletBinding()]
param(
    [string]$ComputerName = "."
)

$target = if ([string]::IsNullOrWhiteSpace($ComputerName) -or $ComputerName -eq ".") {
    $env:COMPUTERNAME
} else {
    $ComputerName
}

Write-Information "Resolving IP info for $target ..." -InformationAction Continue

# Get-NetIPAddress returns rich objects. ScriptDeck's PowerShellExecutor
# detects them as 'structured' (non-primitive BaseObject) and renders
# the property names as grid columns automatically.
try {
    if ($target -eq $env:COMPUTERNAME -or $target -eq "." -or $target -eq "localhost") {
        Get-NetIPAddress |
            Where-Object { $_.AddressFamily -eq 'IPv4' -and $_.PrefixOrigin -ne 'WellKnown' } |
            Select-Object InterfaceAlias, IPAddress, PrefixLength, PrefixOrigin, AddressState
    } else {
        # Remote: try CIM; fall back to a friendly note if it fails.
        Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -ComputerName $target |
            Where-Object { $_.IPAddress } |
            Select-Object @{n='InterfaceAlias';e={$_.Description}},
                          @{n='IPAddress';e={$_.IPAddress -join ', '}},
                          @{n='Gateway';e={$_.DefaultIPGateway -join ', '}},
                          @{n='DHCPEnabled';e={$_.DHCPEnabled}}
    }
} catch {
    Write-Warning "Lookup failed: $($_.Exception.Message)"
}
