# Per thread: name, the library of the frame that started it (the one just below start_thread), and the chain of
# libraries from the innermost runtime frame outward (libc frames dropped). Usage: summarize.py <child-*.txt>...
import re, sys, os
for path in sys.argv[1:]:
    text = open(path).read()
    print(f"== {os.path.basename(path)}: {text.splitlines()[0]}")
    bt = text.split("## thread apply all bt", 1)[1]
    for block in re.split(r"\n(?=Thread \d+ \()", bt):
        m = re.match(r'Thread (\d+) \(Thread \S+ \(LWP (\d+)\) "([^"]*)"\)', block.strip())
        if not m: continue
        libs = []
        for line in block.splitlines():
            f = re.search(r"from \S*/(lib[^/ ]+\.so)", line)
            if f: libs.append(f.group(1))
            elif "start_thread" in line: libs.append("start_thread")
            elif re.match(r"#\d+\s+0x[0-9a-f]+ in \?\? \(\)$", line.strip()): libs.append("jit?")
        starter = libs[libs.index("start_thread") - 1] if "start_thread" in libs and libs.index("start_thread") > 0 else "-"
        chain = []
        for l in libs:
            if l != "start_thread" and (not chain or chain[-1] != l): chain.append(l)
        print(f"  {m.group(3):<16} lwp {m.group(2):<7} started by {starter:<22} chain {' > '.join(chain)}")
