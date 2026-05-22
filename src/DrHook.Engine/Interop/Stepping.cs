// Stepping via ICorDebugStepper. Raw V-table (slots from cordebug.idl order), validated by
// probe 13. Called on the pump worker thread at a stop (process synchronized): create a stepper
// on the stopped thread, arm the step, release our stepper ref (the runtime owns the active
// step), then the worker Continues. Completion arrives as a StepComplete callback — which the
// pump classifies as StopReason.Step — so stepping composes with the stopping model with no new
// callback wiring, exactly like breakpoints.
//
// v1 is IL-granularity (Step/StepOut, no COR_DEBUG_STEP_RANGE) — source-line stepping needs PDB
// line mapping (a later refinement). StepOut runs to the caller; Step in/over single-steps,
// descending into calls (in) or not (over).

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class Stepping
{
    private const int ThreadCreateStepper = 12; // ICorDebugThread (IUnknown 0-2, own 3-)
    private const int StepperStep = 7;          // ICorDebugStepper (IsActive3..SetUnmappedStopMask6, Step7)
    private const int StepperStepOut = 9;       // (StepRange8, StepOut9)

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    /// <summary>Arm a step on <paramref name="pThread"/> per <paramref name="kind"/>. No-op for
    /// <see cref="ResumeKind.Continue"/>. The caller Continues afterward.</summary>
    public static void Arm(nint pThread, ResumeKind kind)
    {
        if (kind == ResumeKind.Continue) return;

        nint stepper = CreateStepper(pThread);
        if (stepper == 0) return;
        try
        {
            switch (kind)
            {
                case ResumeKind.StepInto: Step(stepper, stepIn: 1); break;
                case ResumeKind.StepOver: Step(stepper, stepIn: 0); break;
                case ResumeKind.StepOut: StepOut(stepper); break;
            }
        }
        finally
        {
            // The runtime holds the active step; drop our reference.
            RuntimeNavigation.Release(stepper);
        }
    }

    private static nint CreateStepper(nint pThread)
    {
        // ICorDebugThread.CreateStepper(ICorDebugStepper** ppStepper)
        var create = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(pThread, ThreadCreateStepper);
        nint stepper;
        return create(pThread, &stepper) < 0 ? 0 : stepper;
    }

    private static void Step(nint pStepper, int stepIn)
        => ((delegate* unmanaged[Cdecl]<nint, int, int>)Slot(pStepper, StepperStep))(pStepper, stepIn);

    private static void StepOut(nint pStepper)
        => ((delegate* unmanaged[Cdecl]<nint, int>)Slot(pStepper, StepperStepOut))(pStepper);
}
