# Builds voice-dictate.exe from dictate.cs.
# Windows PowerShell 5.1 only: PowerShell 7's Add-Type has no -OutputAssembly.
$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -eq "Core") {
    throw "Run this with Windows PowerShell 5.1 (powershell.exe), not PowerShell 7."
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "voice-dictate.exe"
$source = [IO.File]::ReadAllText((Join-Path $root "dictate.cs"), [Text.Encoding]::UTF8)

Add-Type -TypeDefinition $source `
    -ReferencedAssemblies System.Windows.Forms, System.Drawing `
    -OutputAssembly $exe -OutputType WindowsApplication

Write-Host "built $exe"
