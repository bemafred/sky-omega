# For threads whose stack touches libmscordbi/libmscordaccore: each frame as lib+offset (offset from the library's
# text start in "info sharedlibrary", identical across runs of one build), plus the innermost libc wait call.
import re, sys, os
for path in sys.argv[1:]:
    text = open(path).read()
    base = {}
    for m in re.finditer(r"^(0x[0-9a-f]+)\s+0x[0-9a-f]+\s+\S+\s+(?:\(\*\)\s+)?\S*/(lib[^/ ]+\.so)$", text, re.M):
        base[m.group(2)] = int(m.group(1), 16)
    print(f"== {os.path.basename(path)}")
    bt = text.split("## thread apply all bt", 1)[1]
    for block in re.split(r"\n(?=Thread \d+ \()", bt):
        if "libmscord" not in block: continue
        name = re.search(r'\(LWP (\d+)\) "([^"]*)"', block)
        frames, wait = [], None
        for line in block.splitlines():
            m = re.match(r"#\d+\s+(0x[0-9a-f]+) in \?\? \(\) from \S*/(lib[^/ ]+\.so)", line.strip())
            if m and m.group(2) in base:
                lib = m.group(2).replace("libmscordaccore.so", "dac").replace("libmscordbi.so", "dbi").replace("libcoreclr.so", "clr")
                frames.append(f"{lib}+{int(m.group(1),16)-base[m.group(2)]:#x}")
            elif wait is None:
                w = re.search(r"(___pthread_cond_\w+|___pthread_mutex_lock|__GI___poll|__libc_open64|__GI___libc_read|__libc_recv\w*|__GI___nanosleep|epoll_wait|\w*waitpid\w*)", line)
                if w: wait = w.group(1)
        print(f"  {name.group(2):<16} {wait or '?':<26} {' '.join(frames[:9])}")
