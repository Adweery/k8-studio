$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
$taskWpf = Join-Path $taskFramework 'WPF'
$taskArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', "/out:$PSScriptRoot\K8Studio.exe", "/resource:$PSScriptRoot\MainWindow.xaml,MainWindow.xaml", "/reference:$taskWpf\PresentationFramework.dll", "/reference:$taskWpf\PresentationCore.dll", "/reference:$taskWpf\WindowsBase.dll", '/reference:System.Xaml.dll', "$PSScriptRoot\UsbDevice.cs", "$PSScriptRoot\StudioProtocol.cs", "$PSScriptRoot\StudioWindow.cs", "$PSScriptRoot\StudioTests.cs")
$taskArgs += "/win32manifest:$PSScriptRoot\app.manifest"
if (Test-Path -LiteralPath "$PSScriptRoot\K8Studio.ico") { $taskArgs += "/win32icon:$PSScriptRoot\K8Studio.ico" }
& $taskCompiler @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Write-Output "Built: $PSScriptRoot\K8Studio.exe"

