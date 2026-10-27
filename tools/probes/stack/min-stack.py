#!/usr/bin/env python3
"""ADR-055 Q3: smallest thread stack each query shape completes on, fully optimized and at Tier 0.
Usage: min-stack.py <shape,shape,...> <min-stack.dll>"""
import subprocess,sys,os
dll=sys.argv[2]
def ok(kb,kind,env):
    r=subprocess.run(["dotnet",dll,str(kb),kind],env={**os.environ,**env},capture_output=True,text=True,timeout=120)
    if r.returncode==3: return None  # query error, not a stack issue
    return r.returncode==0
def minkb(kind,env):
    hi=4096
    if not ok(hi,kind,env): return f">{hi}"
    lo=8
    while hi-lo>8:
        mid=(lo+hi)//2
        res=ok(mid,kind,env)
        if res is None: return "query error"
        if res: hi=mid
        else: lo=mid
    return hi
kinds=sys.argv[1].split(",")
for mode,env in [("FullOpts",{"DOTNET_TieredCompilation":"0"}),("Tier0",{"DOTNET_TieredCompilation":"1","DOTNET_TieredPGO":"0","DOTNET_TC_QuickJitForLoops":"1","DOTNET_OSR_HitLimit":"0"})]:
    for k in kinds: print(f"{mode:9} {k:12} min stack ≈ {minkb(k,env)} KB",flush=True)
