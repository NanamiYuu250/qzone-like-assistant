$ErrorActionPreference = 'Stop'

$appName = 'QQ' + [char]0x7A7A + [char]0x95F4 + [char]0x70B9 +
    [char]0x8D5E + [char]0x52A9 + [char]0x624B
$installDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$executablePath = Join-Path $installDirectory ($appName + '.exe')

if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Application executable was not found: $executablePath"
}

$desktopDirectory = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktopDirectory ($appName + '.lnk')
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executablePath
$shortcut.WorkingDirectory = $installDirectory
$shortcut.IconLocation = "$executablePath,0"
$shortcut.Description = $appName + ' V1.10'
$shortcut.Save()

Write-Host "Desktop shortcut created: $shortcutPath"
