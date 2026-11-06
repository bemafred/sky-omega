# Names the runtime frames of every thread that touches mscordbi, in gdb samples made by gdb-holder.py, with
# addr2line and the runtime's symbol files (dotnet-symbol --symbols; build ids must match).
# Usage: symbolize.py <symbols dir> <sample>...   A frame's address is a return address, so addr2line gets address-1.
import re, sys, subprocess, os
sy, paths = sys.argv[1], sys.argv[2:]
LIBS = ("libmscordaccore.so", "libmscordbi.so", "libcoreclr.so")
def name(lib, off):
    out = subprocess.run(["addr2line", "-f", "-C", "-e", f"{sy}/{lib}.dbg", hex(off - 1)], capture_output=True, text=True).stdout.split("\n")
    fn = re.sub(r"\(.*", "", out[0]) if out and out[0] else "?"
    loc = re.sub(r".*/src/coreclr/", "", out[1]) if len(out) > 1 else ""
    return f"{fn} ({loc})"
for path in paths:
    text = open(path).read()
    print(f"== {os.path.basename(path)}: {text.splitlines()[0]}")
    base = {m.group(2): int(m.group(1), 16) for m in re.finditer(r"^(0x[0-9a-f]+)\s+0x[0-9a-f]+\s+\S+\s+(?:\(\*\)\s+)?\S*/(lib[^/ ]+\.so)$", text, re.M)}
    bt = text.split("## thread apply all bt", 1)[1]
    for block in re.split(r"\n(?=Thread \d+ \()", bt):
        if "libmscordbi" not in block: continue
        head = re.search(r'\(LWP (\d+)\) "([^"]*)"', block)
        lines, mutex = [], re.search(r"___pthread_mutex_lock \(mutex=(0x[0-9a-f]+)\)", block)
        for line in block.splitlines():
            w = re.search(r"(___pthread_cond_wait|___pthread_cond_timedwait64|___pthread_mutex_lock|__GI___libc_read)\b", line)
            if w and not lines: lines.append(f"    libc {w.group(1)}")
            m = re.match(r"#\d+\s+(0x[0-9a-f]+) in \?\? \(\) from \S*/(lib[^/ ]+\.so)", line.strip())
            if m and m.group(2) in LIBS:
                off = int(m.group(1), 16) - base[m.group(2)]
                lines.append(f"    {m.group(2)[3:-3]}+{off:#x} {name(m.group(2), off)}")
        if mutex:
            off = int(mutex.group(1), 16) - base["libmscordaccore.so"]
            sym = subprocess.run(["nm", "-C", f"{sy}/libmscordaccore.so.dbg"], capture_output=True, text=True).stdout
            hit = [l.split()[-1] for l in sym.splitlines() if l.startswith(f"{off:016x}")]
            lines.insert(1, f"    mutex at mscordaccore+{off:#x} = {', '.join(hit) or '?'}")
        print(f"  {head.group(2)} (lwp {head.group(1)})")
        print("\n".join(lines))
    print()
