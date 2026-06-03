// DrHook.Engine probe 62 TARGET (compiled). A long-running heartbeat loop that writes an
// incrementing counter to a FILE (args[0]) and NEVER to Console — so probe 62 isolates F-010-2
// Detach-survival from the console-pipe (D2/D4) unknown. DebugSession.Launch attaches before Main
// via the entry-module hold-gate; the probe resumes past the hold, lets the loop run, Pause-stops
// it (the synchronized "induced stop"), then DetachLeaveRunning and asserts the heartbeat keeps
// advancing — the Owned target left running un-debugged. The atomic temp-then-move write keeps the
// probe's concurrent reads from seeing a partially written value.

using System;
using System.Globalization;
using System.IO;
using System.Threading;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: LeaveRunning <heartbeat-file>");
    return 2;
}

string beatFile = args[0];
string tmp = beatFile + ".tmp";

// ~60 s of heartbeats at 100 ms — long enough to observe survival across a detach and the probe's
// own exit, bounded so a missed cleanup cannot orphan the process indefinitely.
for (int beat = 1; beat <= 600; beat++)
{
    File.WriteAllText(tmp, beat.ToString(CultureInfo.InvariantCulture));
    File.Move(tmp, beatFile, overwrite: true);
    GC.KeepAlive(beat);   // BEAT_HERE — probe 62b arms a line breakpoint here (62 ignores it)
    Thread.Sleep(100);
}

return 0;
