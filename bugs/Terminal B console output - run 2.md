C:\Users\marfr175\source\repos\sky-omega\tools\SemaphoreTest>dotnet run -- crossprocess 3
=== Cross-Process Coordination Test ===
PID: 27976
Semaphore: Global\SkyOmega-QuadStore-Gate-v1
Max slots: 4

Semaphore created new: True

Attempting to acquire 3 slots...
Slot 1: ACQUIRED
Slot 2: ACQUIRED
Slot 3: BLOCKED (another process holds slots)

Holding 2 slots.

=== TO TEST CROSS-PROCESS COORDINATION ===
1. Run this in Terminal A: dotnet run -- crossprocess 3
2. While A is running, run in Terminal B: dotnet run -- crossprocess 3
3. Terminal B should BLOCK because A holds 3 of 4 slots

Press Enter to release slots and exit...