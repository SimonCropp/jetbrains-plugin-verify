Param(
    $Version = "9999.0.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSScriptRoot = Split-Path $MyInvocation.MyCommand.Path -Parent
Set-Location $PSScriptRoot

. ".\settings.ps1"

Invoke-DotNetPack "-p:PackageVersion=$Version" "-p:PackageOutputPath=$OutputDirectory"
