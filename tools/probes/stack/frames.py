#!/usr/bin/env python3
"""ADR-055 Q3: stack frame size per method, from a DOTNET_JitDisasm listing (arm64 prologues).
Usage: frames.py <listing> <method-regex> <top-n>"""
import re,sys
def frames(path):
    out={}; cur=None; tier=None; acc=0; regs={}; inprolog=False
    for line in open(path, errors='replace'):
        m=re.match(r"; Assembly listing for method (\S+?)(\(|:)",line)
        if m:
            if cur: out.setdefault(cur,[]).append((tier,acc))
            full=line.split("for method ",1)[1].strip()
            cur=re.sub(r"\(.*","",full); tier=re.search(r"\((Tier0|Tier1|FullOpts|Instrumented Tier0|Tier1-OSR|OSR)[^)]*\)\s*$",full); tier=tier.group(1) if tier else "?"
            acc=0; regs={}; inprolog=True; continue
        if not cur or not inprolog: continue
        if re.match(r"G_M\d+_IG02:",line.strip()): inprolog=False; continue
        s=line.strip()
        m=re.match(r"(movz|mov|movn)\s+(x\d+), #(0x[0-9A-Fa-f]+|\d+)(?:, LSL #(\d+))?",s)
        if m:
            v=int(m.group(3),0); 
            if m.group(4): v<<=int(m.group(4))
            if m.group(1)=="movn": v=~v
            regs[m.group(2)]=v
        m=re.match(r"movk\s+(x\d+), #(0x[0-9A-Fa-f]+|\d+), LSL #(\d+)",s)
        if m and m.group(1) in regs: regs[m.group(1)]|=int(m.group(2),0)<<int(m.group(3))
        m=re.match(r"stp\s+\S+, \S+, \[sp, #-(0x[0-9A-Fa-f]+|\d+)\]!",s)
        if m: acc+=int(m.group(1),0)
        m=re.match(r"str\s+\S+, \[sp, #-(0x[0-9A-Fa-f]+|\d+)\]!",s)
        if m: acc+=int(m.group(1),0)
        m=re.match(r"sub\s+sp, sp, #(0x[0-9A-Fa-f]+|\d+)(?:, LSL #(\d+))?",s)
        if m:
            v=int(m.group(1),0)
            if m.group(2): v<<=int(m.group(2))
            acc+=v
        m=re.match(r"sub\s+sp, sp, (x\d+)",s)
        if m and m.group(1) in regs: acc+=regs[m.group(1)]
    if cur: out.setdefault(cur,[]).append((tier,acc))
    return out
path=sys.argv[1]; pat=re.compile(sys.argv[2]); top=int(sys.argv[3])
f=frames(path)
rows=[(max(a for t,a in v),k,",".join(sorted({t for t,a in v}))) for k,v in f.items() if pat.search(k)]
rows.sort(reverse=True)
for a,k,t in rows[:top]: print(f"{a:8,}  {k.replace('SkyOmega.Mercury.Sparql.','')}  [{t}]")
print("methods:",len(rows))
