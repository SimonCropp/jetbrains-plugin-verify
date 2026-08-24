Param(
    $RootSuffix = "Verify",
    $Version = "9999.0.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSScriptRoot = Split-Path $MyInvocation.MyCommand.Path -Parent
Set-Location $PSScriptRoot

. ".\settings.ps1"

# Visual Studio. Only this script needs one - it installs a ReSharper experimental hive into an
# installation and then launches devenv against it - so the lookup lives here rather than in
# settings.ps1, where every script that dot-sourced it paid for a dependency it did not have.
#
# Matched by devenv.exe rather than by "-products *", which is not the question "where is Visual
# Studio". Other Microsoft installers register through the same setup API and come back from it:
# SQL Server Management Studio 22 reports channelId "SSMS.22.SSMS.Release" and version 22.8.x, so
# it matched "Release", sorted above Visual Studio's 17.x, and won. Build Tools is returned too and
# has no IDE to launch. What this script wants is an installation it can start, so that is what is
# looked for.
$VsWhereOutput = [xml] (& "$PSScriptRoot\tools\vswhere.exe" -format xml -products *)
$VisualStudio = $VsWhereOutput.instances.instance |
    Where-Object { $_.channelId -match "Release" } |
    Where-Object { Test-Path "$($_.installationPath)\Common7\IDE\devenv.exe" } |
    Sort-Object -Property installationVersion |
    Select-Object -Last 1

if (-Not $VisualStudio) {
    throw "No Visual Studio installation with devenv.exe was found. This script installs a ReSharper experimental hive and launches it, so it needs Visual Studio rather than Build Tools. Use buildPlugin.ps1 to build without one."
}

$VisualStudioMajorVersion = ($VisualStudio.installationVersion -split '\.')[0]
$VisualStudioInstanceId = $VisualStudio.instanceId
$DevEnvPath = "$($VisualStudio.installationPath)\Common7\IDE\devenv.exe"

$UserProjectXmlFile = "$SourceBasePath\$PluginId\$PluginId.csproj.user"

if (!(Test-Path "$UserProjectXmlFile")) {
    # Get versions from Plugin.props file
    $PluginPropsFile = "$SourceBasePath\Plugin.props"
    $PluginPropsXml = [xml] (Get-Content "$PluginPropsFile")
    $SdkVersionNode = $PluginPropsXml.SelectSingleNode(".//SdkVersion")
    $VersionSplit = $SdkVersionNode.InnerText.Split(".")
    $MajorVersion = "$($VersionSplit[0]).$($VersionSplit[1])"

    # Determine download link
    $ReleaseUrl = "https://data.services.jetbrains.com/products/releases?code=RSU&type=eap&type=release&majorVersion=$MajorVersion"
    $VersionEntry = $(Invoke-WebRequest -UseBasicParsing $ReleaseUrl | ConvertFrom-Json).RSU[0]
    ## TODO: check versions
    $DownloadLink = [uri] ($VersionEntry.downloads.windows.link.replace(".exe", ".Checked.exe"))

    # Download installer
    $InstallerFile = "$env:TEMP\JetBrains\Installer.Offline\$($DownloadLink.Segments[-1])"
    if (!(Test-Path $InstallerFile)) {
        Write-Output "Downloading $($DownloadLink.Segments[-2].TrimEnd("/")) installer"
        Start-BitsTransfer -Source $DownloadLink -Destination $InstallerFile
    } else {
        Write-Output "Using cached installer from $InstallerFile"
    }

    # Execute installer
    Write-Output "Installing experimental hive"
    Invoke-Exe $InstallerFile "/VsVersion=$VisualStudioMajorVersion.0" "/SpecificProductNames=ReSharper" "/Hive=$RootSuffix" "/Silent=True"

    $Installations = @(Get-ChildItem "$env:LOCALAPPDATA\JetBrains\ReSharperPlatformVs$VisualStudioMajorVersion\vAny_$VisualStudioInstanceId$RootSuffix\NuGet.Config")
    if ($Installations.Count -ne 1) { Write-Error "Found no or multiple installation directories: $Installations" }
    $InstallationDirectory = $Installations.Directory
    Write-Host "Found installation directory at $InstallationDirectory"

    # Adapt packages.config
    if (Test-Path "$InstallationDirectory\packages.config") {
        $PackagesXml = [xml] (Get-Content "$InstallationDirectory\packages.config")
    } else {
        $PackagesXml = [xml] ("<?xml version=`"1.0`" encoding=`"utf-8`"?><packages></packages>")
    }

    if ($null -eq $PackagesXml.SelectSingleNode(".//package[@id='$PluginId']/@id")) {
        $PluginNode = $PackagesXml.CreateElement('package')
        $PluginNode.setAttribute("id", "$PluginId")
        $PluginNode.setAttribute("version", "$Version")

        $PackagesNode = $PackagesXml.SelectSingleNode("//packages")
        $PackagesNode.AppendChild($PluginNode) > $null

        $PackagesXml.Save("$InstallationDirectory\packages.config")
    }

    # Adapt user project file
    $HostIdentifier = "$($InstallationDirectory.Parent.Name)_$($InstallationDirectory.Name.Split('_')[-1])"

    Set-Content -Path "$UserProjectXmlFile" -Value "<Project><PropertyGroup Condition=`"'`$(MSBuildRuntimeType)' == 'Full'`"><HostFullIdentifier></HostFullIdentifier></PropertyGroup></Project>"

    $ProjectXml = [xml] (Get-Content "$UserProjectXmlFile")
    $HostIdentifierNode = $ProjectXml.SelectSingleNode(".//HostFullIdentifier")
    $HostIdentifierNode.InnerText = $HostIdentifier
    $ProjectXml.Save("$UserProjectXmlFile")

    # Install plugin
    $PluginRepository = "$env:LOCALAPPDATA\JetBrains\plugins"
    Remove-Item "$PluginRepository\${PluginId}.${Version}" -Recurse -ErrorAction Ignore
    Invoke-DotNetPack "-p:PackageVersion=$Version" "-p:PackageOutputPath=$OutputDirectory"
    Invoke-Exe $NuGetPath install $PluginId -OutputDirectory "$PluginRepository" -Source "$OutputDirectory" -DependencyVersion Ignore

    Write-Output "Re-installing experimental hive"
    Invoke-Exe "$InstallerFile" "/VsVersion=$VisualStudioMajorVersion.0" "/SpecificProductNames=ReSharper" "/Hive=$RootSuffix" "/Silent=True"
} else {
    Write-Warning "Plugin is already installed. To trigger reinstall, delete $UserProjectXmlFile."
}

Invoke-DotNetBuild
Invoke-Exe $DevEnvPath "/rootSuffix $RootSuffix" "/ReSharper.Internal" "/ReSharper.LogFile $PSScriptRoot\ReSharper.log" "/ReSharper.LogLevel Trace"
