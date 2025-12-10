using namespace System.Xml.Linq
using namespace System.Xml.XPath

$Properties = [XElement]::Load("Build.props")
$ModDeployDir = [System.Xml.XPath.Extensions]::XPathSelectElement($Properties, "/PropertyGroup/ModDeployDir").Value

$DeployList = @{
    "ClientProject\ClientSource" = "$($ModDeployDir)CSharp\Client"
}

$Option = Read-Host (
    "Choices" `
        + " ($($PSStyle.Foreground.Yellow)$($PSStyle.Blink)D$($PSStyle.Reset)eploy/" `
        + "$($PSStyle.Foreground.Yellow)$($PSStyle.Blink)U$($PSStyle.Reset)ndeploy/" `
        + "$($PSStyle.Foreground.Yellow)$($PSStyle.Blink)E$($PSStyle.Reset)xit)"
)

switch ($Option.ToLower()) {
    { $_ -eq 'd' -or $_ -eq 'deploy' } { 
        foreach ($pair in $DeployList.GetEnumerator()) {
            Robocopy.exe $pair.Key $pair.Value /MIR /XC /FP
        }
        if (-not (Test-Path -Path "$($ModDeployDir)CSharp" -PathType Container)) {
            New-Item -Path "$($ModDeployDir)CSharp" -ItemType Directory
        }
        Copy-Item -Path "Assets\RunConfig.xml" -Destination "$($ModDeployDir)CSharp\RunConfig.xml" -Force
        if (Test-Path -Path "$($ModDeployDir)bin" -PathType Container) {
            Remove-Item -Path "$($ModDeployDir)bin" -Recurse
        }
        
    }
    { $_ -eq 'u' -or $_ -eq 'undeploy' } { 
        Remove-Item -Path "$($ModDeployDir)CSharp" -Recurse
    }
    { $_ -eq 'e' -or $_ -eq 'exit' } { 
        exit
    }
}
