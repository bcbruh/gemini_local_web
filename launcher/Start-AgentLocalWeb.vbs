Option Explicit

Dim shell, fileSystem, launcherDirectory, command
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

launcherDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
command = "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass " & _
    "-WindowStyle Hidden -File """ & launcherDirectory & "\Start-AgentLocalWeb.ps1"""

shell.Run command, 0, False
