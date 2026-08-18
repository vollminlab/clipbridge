#Requires AutoHotkey v2.0
#SingleInstance Force

; clipbridge.ahk - types a devsbx01 path into the focused terminal after a
; clipboard image round-trips through Send-Clip.ps1.
;
; Ctrl+V (Windows Terminal only):       image on clipboard -> send it, type
;                                        the returned path; anything else,
;                                        or any failure -> ordinary paste.
; Ctrl+Shift+V (Windows Terminal only): force a send regardless of what
;                                        else is on the clipboard. Also
;                                        scoped to the terminal, not global:
;                                        Ctrl+Shift+V is paste-as-plain-text
;                                        in Chrome, VS Code, Slack, Teams and
;                                        most editors, and typing a devsbx01
;                                        path into one of those is never
;                                        useful - so the force-send behavior
;                                        only makes sense where the path is
;                                        actually wanted.
;
; This file holds no clipboard, file, or network logic beyond a bare format
; check and a plain text read - every real decision (what counts as an
; image, how the transfer happens, what a path must look like before it is
; safe to type) lives in Send-Clip.ps1, which is unit-tested. This file
; CANNOT be exercised in CI or by this agent: there is no AutoHotkey
; interpreter and no Windows clipboard available here. It has been reviewed
; by eye only and is UNTESTED.
;
; The single most important rule below: every failure path falls through to
; Send("^v") - an ordinary Ctrl+V paste for a text clipboard, which is the
; critical case this exists to protect. For an image clipboard, Windows
; Terminal has nothing to paste, so Send("^v") pastes nothing visible; the
; user gets an error beep and a tray tip instead, same as if clipbridge were
; not installed at all. Either way, nothing here silently eats a keypress -
; a hotkey that does that when devsbx01 is unreachable is worse than no tool
; at all.

CONFIG_DIR := EnvGet("LOCALAPPDATA") . "\clipbridge"
SEND_CLIP  := A_ScriptDir . "\Send-Clip.ps1"
LOG_PATH   := CONFIG_DIR . "\clipbridge.log"

#HotIf WinActive("ahk_exe WindowsTerminal.exe")
^v:: {
    if (!ClipboardHasImage() || !RunClipbridge())
        Send("^v")          ; not an image, or the send failed: ordinary paste
}

^+v:: {
    if (!RunClipbridge())
        Send("^v")
}
#HotIf

; A format-availability check only (CF_BITMAP / CF_DIB) - it never reads the
; clipboard's actual data, just asks Windows what formats are on offer. This
; exists purely so a plain-text Ctrl+V in the terminal stays instant instead
; of paying a PowerShell-startup delay on every keystroke; it makes no
; decision about validity or content - that is Send-Clip.ps1's job via its
; own exit code (2 = no image).
ClipboardHasImage() {
    return DllCall("IsClipboardFormatAvailable", "UInt", 2)   ; CF_BITMAP
        || DllCall("IsClipboardFormatAvailable", "UInt", 8)   ; CF_DIB
}

; Runs Send-Clip.ps1 and acts on its exit code. Returns true only when a
; path was actually typed - both callers above fall through to an ordinary
; paste on any false return.
;
; Exit codes, from Send-Clip.ps1's own doc comment:
;   0 ok | 2 no image | 3 remote rejected input | 4 ssh failed
;   5 remote cannot write | 6 no usable path returned
;   7 cannot write local temp file | 8 configuration problem
; 2 is expected, ordinary behavior - nothing usable was on the clipboard.
; Everything else, including 7 and 8 (both local failures on this laptop -
; a full disk or temp dir problem, or a missing/broken config.json - rather
; than anything to do with devsbx01 or the network) is a failure: beep,
; name the log, and fall through to paste. The log line Send-Clip.ps1 wrote
; already distinguishes the exact cause; this script only needs to point at
; it, not diagnose it.
RunClipbridge() {
    lastPath := CONFIG_DIR . "\last-path.txt"
    ; Delete first so a stale path from a previous run can never be typed
    ; if this run fails before writing a fresh one.
    try FileDelete(lastPath)

    ; RunWait, never a poll loop: Send-Clip.ps1 writes last-path.txt with
    ; Set-Content, which truncates then writes rather than writing
    ; atomically, so the file is briefly empty or partial mid-write. It is
    ; only safe to read after the process has fully exited, which is
    ; exactly what RunWait's return guarantees and a poll loop would not.
    code := RunWait('powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File "' . SEND_CLIP . '"', , "Hide")

    if (code = 2) {
        SoundBeep(600, 80)
        SoundBeep(400, 80)
        return false
    }
    if (code != 0) {
        SoundBeep(300, 200)
        TrayTip("clipbridge failed (exit " . code . ") - see " . LOG_PATH, "clipbridge", 3)
        return false
    }

    path := ""
    try path := Trim(FileRead(lastPath))
    if (path = "") {
        SoundBeep(300, 200)
        TrayTip("clipbridge exited 0 but wrote no path - see " . LOG_PATH, "clipbridge", 3)
        return false
    }

    SendText(path . " ")
    SoundBeep(900, 60)
    return true
}
