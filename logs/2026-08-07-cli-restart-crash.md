# CLI agent restart crash when toggling the Cody MCP

## Goal

Toggling "Cody tools" off while a hosted CLI agent is running must not crash the app. The toggle
restarts the console, because a CLI reads its MCP configuration once at launch.

## Symptom

Reported three times. The user describes it as a size problem, "the same issue as loading the agent
cli" — that first crash was WinUI rejecting a size the hosted console reported.

## Attempts that did NOT fix it

Do not repeat these on their own; all three are in the code and all three left the crash in place.

1. Teardown order in `CliAgentPanel.StopSession`. Was killing the process tree with `Kill(true)`
   before `DisconnectConPTYTerm()`. Reordered to match `CodyPage.CancelTerminal`: hide, disconnect,
   close stdin, stop terminal, remove from tree, and only then kill. Each step got its own guard.
2. Deferred restart. The replacement console was created in the same pass as the teardown. Moved to
   a low-priority dispatcher hop, then to a 250 ms one-shot `DispatcherQueueTimer`
   (`StartSessionAfterTeardown`), plus a guard refusing to start while `TerminalHost.Children` is
   not empty.
3. Bounded measure. `TerminalHost.Height` is now pinned to a pixel value before a console is added
   and on every resize, and sizes are measured from the panel and its header rather than from the
   host itself. This mirrors `ResizeInteractiveTerminal`, which pins `InteractiveTerminalHost.Height`
   and measures from `TerminalPanel`.

Also already in place from the first crash, and believed correct: start only once the host has a
real size (`_startWhenSized`), create the console collapsed, reveal at low priority once arranged.

## Current step: instrumentation

Added `App\Services\DiagnosticLog.cs`, appending to
`%USERPROFILE%\crster\utility\logs\cli-agent-diagnostics.log`.

- `App.xaml.cs` hooks `Application.UnhandledException`, `AppDomain.CurrentDomain.UnhandledException`
  and `TaskScheduler.UnobservedTaskException`, so a background failure is captured too.
- `CliAgentPanel` traces `Activate`, every step of `StopSession`, the restart timer tick,
  `StartSession` (with the measured width, height, panel size, header height, host visibility and
  suppression state), the theme re-apply, the reveal and every resize. Every previously silent
  `catch` now writes its exception.
- `CodyPage` traces the toggle change and the MCP teardown.

## ROOT CAUSE FOUND (from the log, 2026-08-07 00:32)

Not a size problem. The restart completed cleanly at 00:32:11.423 — console created, revealed,
resized to 735x476. The crash came at 00:32:17.585, six seconds later, on a focus change:

```
System.ArgumentException: Value does not fall within the expected range. (HResult 0x80070057)
  at Microsoft.UI.Xaml.Input.GettingFocusEventArgs.set_Cancel(Boolean value)
  at Microsoft.Terminal.Wpf.TerminalControl.TerminalControl_GettingFocus(UIElement, GettingFocusEventArgs)
  at System.Threading.Tasks.Task.<>c.<ThrowAsync>b__124_0(Object state)
  at Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext...Post
```

`TerminalControl` hands focus to its native child window by cancelling the XAML focus move. Once the
control has been detached, setting `Cancel` is rejected with E_INVALIDARG. The throw escapes the
control's own event handler and is reposted to the dispatcher, so it cannot be caught at any call
site of ours. It only happens after a restart, because a restart is the only thing that leaves a
discarded console behind, and it fires on the next focus change rather than during the restart —
which is why it looked unrelated to the toggle and why all three earlier fixes missed.

## Fix

1. `CliAgentPanel.StopSession` now takes focus off the dying console and makes it unfocusable
   (`IsEnabled`, `IsTabStop`, `IsHitTestVisible` on both the wrapper and its inner terminal) before
   detaching it, moving focus to the Restart button if the console held it. A disabled element is
   not raised GettingFocus, so the handler never runs.
2. `App.xaml.cs` marks this one exception handled, matched narrowly on `ArgumentException` plus
   `TerminalControl_GettingFocus` in the stack. Needed because the throw originates inside the
   third-party handler and can be reached by focus paths we do not control.

## Second log run (00:36) — the focus release did NOT prevent the throw

- `StopSession focus released hadFocus=False` on every teardown. The XAML focused element is never
  the wrapper or its inner terminal, so disabling them changed nothing. The native child window
  holds real Win32 focus, and disabling a XAML element does not stop that window being focused; the
  control then forwards it into XAML and its GettingFocus handler throws.
- The faults land 2 s and 4 s AFTER the restart finished, twice per restart, i.e. on later focus
  changes, not during teardown.
- The App-level guard works: two `ignored hosted terminal focus fault` lines per restart, no crash.

## Real fix: never detach the control

`EasyTerminalControl.RestartTerm(TermPTY useTerm = null, bool disposeOld = true)` relaunches the
shell inside the control that is already hosted. `Activate` now uses it whenever the agent is the
same and only the MCP wiring changed — which is exactly the toggle path — so no control is detached
and nothing can throw. It sets `StartupCommandLine` and `WorkingDirectory` first, and falls back to
the old full restart if `RestartTerm` fails.

The full teardown path still exists for a workspace change, the Stop button and window close. Those
do detach a control, so the focus release and the App-level guard both stay.

## Unrelated, surfaced by the logging

`Task.Unobserved: COMException 0x8000000A at CodyPage.LoadSystemIconAsync` — a pre-existing
unobserved task fault in workspace tree icon loading. Harmless, but it should get a catch. Not
touched here; out of scope for this bug.

## Next step

User reproduces the crash once, then read the log. What to look for:

- Does an exception appear at all, and on which source? `Task.Unobserved` or `AppDomain.Unhandled`
  would mean it is a background thread, not the layout pass, and all three fixes above were aimed at
  the wrong place.
- The `StartSession` trace line: are `width`/`height` sane at restart, and is `children` zero?
- Whether the crash lands between `StopSession done` and `RestartTimer tick`, which would point at
  the MCP teardown or the server socket rather than the console.

## Open alternative

Stop restarting on toggle. Record the choice, mark the session as needing a restart, and let the
panel's Restart button apply it. That removes the race instead of timing around it. Offered to the
user; not yet chosen.
