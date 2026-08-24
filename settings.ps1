$PluginId = "ReSharperPlugin.Verify"
$SolutionPath = "$PSScriptRoot\ReSharperPlugin.Verify.slnx"
$SourceBasePath = "$PSScriptRoot\src\dotnet"

$OutputDirectory = "$PSScriptRoot\output"
$NuGetPath = "$PSScriptRoot\tools\nuget.exe"

# `dotnet build` and `dotnet pack`, not Visual Studio's MSBuild. Visual Studio's cannot restore
# these projects: every one is skipped with NU1503, "the project file may be invalid or missing
# targets required for restore", and the build then fails having restored nothing. The .NET SDK is
# already required here, since the solution is an SDK one, and it restores on the way in - so
# neither of these needs a Restore step of its own.
Function Invoke-DotNetBuild {
    param(
        [Parameter(ValueFromRemainingArguments=$true)][String[]] $Arguments
    )

    Invoke-Exe "dotnet" "build" "$SolutionPath" "-v:minimal" @Arguments
}

Function Invoke-DotNetPack {
    param(
        [Parameter(ValueFromRemainingArguments=$true)][String[]] $Arguments
    )

    Invoke-Exe "dotnet" "pack" "$SolutionPath" "-v:minimal" @Arguments
}

Function Invoke-Exe {
    param(
        [parameter(mandatory=$true,position=0)] [ValidateNotNullOrEmpty()] [string] $Executable,
        [Parameter(ValueFromRemainingArguments=$true)][String[]] $Arguments,
        [parameter(mandatory=$false)] [array] $ValidExitCodes = @(0)
    )

    Write-Host "> $Executable $Arguments"
    $rc = Start-Process -FilePath $Executable -ArgumentList $Arguments -NoNewWindow -Passthru
    $rc.Handle # to initialize handle according to https://stackoverflow.com/a/23797762/2684760
    $rc.WaitForExit()
    if (-Not $ValidExitCodes.Contains($rc.ExitCode)) {
        throw "'$Executable $Arguments' failed with exit code $($rc.ExitCode), valid exit codes: $ValidExitCodes"
    }
}
