param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs/Cinemana Desktop/0.4.0'), [switch]$NoShortcut)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
if (!(Test-Path -LiteralPath (Join-Path $source 'Cinemana.Desktop.exe'))) { throw 'Application files are missing. Extract the entire package first.' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $Destination -Recurse -Force
$exe = Join-Path $Destination 'Cinemana.Desktop.exe'
if (!$NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $startMenu = [Environment]::GetFolderPath('Programs')
    foreach ($folder in @($desktop, $startMenu)) {
        $shortcut = $shell.CreateShortcut((Join-Path $folder 'Cinemana Desktop.lnk'))
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = $Destination
        $shortcut.IconLocation = $exe + ',0'
        $shortcut.Save()
    }
}
Write-Output ('Installed: ' + $exe)
Write-Output 'Favorites and watch history remain in LocalAppData/Cinemana.Desktop.'


