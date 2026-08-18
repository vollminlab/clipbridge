#Requires AutoHotkey v2.0
#SingleInstance Force

; ---------------------------------------------------------------------------
; clipbridge verification probe - item 1 of the verification plan.
;
; Question: does a synthetic keystroke burst survive the full input path
;   AHK SendText -> Windows Terminal -> mosh -> tmux -> Claude Code prompt
; without dropping or reordering characters?
;
; This is the single assumption the whole design rests on. If it fails,
; injection has to move back to server-side `tmux send-keys` (already proven
; to work) and the target-selection problem comes back.
;
; HOW TO RUN
;   1. Double-click this file (needs AutoHotkey v2).
;   2. Click into a terminal showing a Claude Code prompt.
;   3. Press a hotkey below. DO NOT press Enter afterwards - nothing here
;      should ever be submitted.
;   4. Compare what landed in the prompt against the MsgBox that follows.
;   5. Clear the prompt with Ctrl+U and move to the next hotkey.
;
;   Ctrl+Shift+F12  production case, default (Input) send mode
;   Ctrl+Shift+F11  same string, Event mode + key delay - the fallback if F12 drops
;   Ctrl+Shift+F10  stress case, 200 chars - reveals a marginal path that 45 chars hides
;
; This script types text and nothing else. It touches no files, no clipboard,
; and no network.
; ---------------------------------------------------------------------------

; A production-shaped path: exactly what clipbridge-recv would return.
PROD := "/home/vollmin/.clipbridge/20260818-024715.png "

; Ordered digits make a dropped or reordered character visible at a glance.
STRESS := ""
Loop 10
    STRESS .= "/home/vollmin/.clipbridge/2026081" . (A_Index - 1) . "-024715.png "

^+F12:: {
    SendText PROD
    Sleep 300
    Report("Input mode (production case)", PROD)
}

^+F11:: {
    prev := A_SendMode
    SendMode "Event"
    SetKeyDelay 10, 10
    SendText PROD
    SendMode prev
    Sleep 300
    Report("Event mode + 10ms key delay", PROD)
}

^+F10:: {
    SendText STRESS
    Sleep 300
    Report("Input mode (stress, " . StrLen(STRESS) . " chars)", STRESS)
}

Report(label, expected) {
    MsgBox(
        "Probe: " . label . "`n`n"
        . "Expected " . StrLen(expected) . " characters:`n`n"
        . expected . "`n`n"
        . "Compare against the prompt. Every character present and in order?`n`n"
        . "Then clear it with Ctrl+U. Do not press Enter.",
        "clipbridge SendText probe",
        "Iconi"
    )
}
