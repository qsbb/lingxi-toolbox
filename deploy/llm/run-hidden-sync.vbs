' Run a batch file hidden; wait for it so the scheduled task stays Running.
' usage: wscript.exe //B run-hidden-sync.vbs "<bat>"
Option Explicit
Dim sh, bat, rc
If WScript.Arguments.Count = 0 Then WScript.Quit 2
bat = WScript.Arguments(0)
Set sh = CreateObject("WScript.Shell")
rc = sh.Run("cmd.exe /c """ & bat & """", 0, True)
WScript.Quit rc
