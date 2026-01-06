# SemaphoreTest

Cross-process semaphore behavior test tool for verifying `CrossProcessStoreGate` coordination.

## Purpose

This tool helps verify that the `CrossProcessStoreGate` correctly coordinates QuadStore slot allocation across multiple processes. This is critical for preventing disk exhaustion when running parallel test processes (e.g., NCrunch).

## Platform Behavior

| Platform | Named Semaphore | CrossProcessStoreGate Strategy |
|----------|-----------------|-------------------------------|
| Windows  | Supported       | Named semaphore (`Global\SkyOmega-QuadStore-Gate-v1`) |
| Linux    | Not supported   | File-based locking |
| macOS    | Not supported   | File-based locking |

## Usage

```bash
cd tools/SemaphoreTest

# Show help
dotnet run

# Test cross-process coordination (interactive)
dotnet run -- crossprocess [slots]

# Test Windows max-count sharing behavior
dotnet run -- maxcount

# Legacy: hold slots interactively
dotnet run -- hold
```

## Cross-Process Coordination Test

This is the main test for verifying the gate works correctly.

### On Windows

1. Open two terminals
2. Terminal A:
   ```bash
   dotnet run -- crossprocess 3
   ```
   Output:
   ```
   === Cross-Process Coordination Test ===
   PID: 1234
   Semaphore: Global\SkyOmega-QuadStore-Gate-v1
   Max slots: 4

   Semaphore created new: True

   Attempting to acquire 3 slots...
     Slot 1: ACQUIRED
     Slot 2: ACQUIRED
     Slot 3: ACQUIRED

   Holding 3 slots.
   Press Enter to release slots and exit...
   ```

3. While Terminal A is running, in Terminal B:
   ```bash
   dotnet run -- crossprocess 3
   ```
   Output:
   ```
   === Cross-Process Coordination Test ===
   PID: 5678
   Semaphore: Global\SkyOmega-QuadStore-Gate-v1
   Max slots: 4

   Semaphore created new: False
     (Opened existing semaphore from another process)

   Attempting to acquire 3 slots...
     Slot 1: ACQUIRED
     Slot 2: BLOCKED (another process holds slots)

   Holding 1 slots.
   ```

4. Terminal B blocks on slot 2 because Terminal A holds 3 of 4 available slots.

5. Press Enter in Terminal A to release slots. Terminal B can then proceed.

### On macOS/Linux

Named semaphores are not supported. The tool will report:
```
Named semaphores not supported on this platform.
CrossProcessStoreGate would use file-based locking instead.
```

The actual `CrossProcessStoreGate` falls back to file-based locking, which uses exclusive file locks in `/tmp/.sky-omega-pool-locks/`.

## Max Count Behavior Test

Tests the Windows behavior where opening an existing named semaphore ignores the max count parameter:

```bash
dotnet run -- maxcount
```

This demonstrates why all `CrossProcessStoreGate` instances must share the same semaphore - the first creator determines the max count.

## How CrossProcessStoreGate Works

```
Process A starts:
  → Creates semaphore "SkyOmega-QuadStore-Gate-v1" with max=6
  → createdNew = true

Process B starts:
  → Opens existing semaphore (same name)
  → createdNew = false
  → max=6 inherited from Process A (Windows ignores the max parameter)

Process A acquires 3 slots → 3 remaining
Process B acquires 2 slots → 1 remaining
Process C tries to acquire 2 → gets 1, blocks on 2nd

Total enforced: 6 slots across all processes
```

## Integration with Tests

The `CrossProcessIntegrationTests` class in `Mercury.Tests` uses this tool to verify cross-process coordination:

```csharp
[Fact]
[Trait("Category", "Integration")]
public void CrossProcess_Coordination_Works()
{
    // Only runs on Windows where named semaphores work
    // Spawns this tool as a child process
    // Verifies child blocks when parent holds slots
}
```

## Files

| File | Purpose |
|------|---------|
| `Program.cs` | Entry point and help |
| `CrossProcessTest.cs` | Main cross-process coordination test |
| `MaxCountTest.cs` | Windows max-count sharing behavior test |
