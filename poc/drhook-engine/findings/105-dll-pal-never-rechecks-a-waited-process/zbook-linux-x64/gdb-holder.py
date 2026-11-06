# Runs probe 100 as a child of gdb, so gdb is an ancestor of every probe child and may attach under
# ptrace_scope 1. Samples each probe child ("<probe> child <mode> <dll>") once it reaches SAMPLE_AGE seconds:
# thread names, shared libraries, and every thread's backtrace. Then detaches.
# Config via env: OUT (dir), SAMPLE_AGE (s, default 45), PROBE_ARGS (space-separated), PRLIMIT_CORE (optional).
import gdb, os, subprocess, time, datetime

OUT = os.environ["OUT"]
SAMPLE_AGE = float(os.environ.get("SAMPLE_AGE", "45"))
ARGS = os.environ["PROBE_ARGS"].split()
os.makedirs(OUT, exist_ok=True)
CLK = os.sysconf("SC_CLK_TCK")

for cmd in ("set pagination off", "set confirm off", "set print thread-events off",
            "set debuginfod enabled off", "set width 0"):
    gdb.execute(cmd)

def now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%H:%M:%S.%f")[:-3] + "Z"

def log(msg):
    with open(os.path.join(OUT, "holder.log"), "a") as f:
        f.write(f"{now()} {msg}\n")

cmd = ["dotnet", "run", "--no-cache", "100-orphaned-read-smoke.cs"] + ARGS
if os.environ.get("PRLIMIT_CORE"):
    cmd = ["prlimit", f"--core={os.environ['PRLIMIT_CORE']}", "--"] + cmd
probe_out = open(os.path.join(OUT, "probe.out"), "w")
probe = subprocess.Popen(cmd, cwd=os.environ["PROBE_DIR"], stdout=probe_out, stderr=subprocess.STDOUT)
log(f"probe pid {probe.pid}: {' '.join(cmd)}")

def stat(pid):
    s = open(f"/proc/{pid}/stat").read()
    rest = s[s.rindex(")") + 2:].split()
    return int(rest[1]), int(rest[19])  # ppid, starttime (ticks)

def descendants(root):
    kids = {}
    for d in os.listdir("/proc"):
        if d.isdigit():
            try:
                ppid, _ = stat(int(d))
                kids.setdefault(ppid, []).append(int(d))
            except (FileNotFoundError, ProcessLookupError, ValueError):
                pass
    out, todo = [], [root]
    while todo:
        p = todo.pop()
        for c in kids.get(p, []):
            out.append(c); todo.append(c)
    return out

def argv(pid):
    try:
        return open(f"/proc/{pid}/cmdline", "rb").read().split(b"\0")
    except FileNotFoundError:
        return []

def sample(pid, age):
    a = argv(pid)
    mode = a[2].decode() if len(a) > 2 else "?"
    path = os.path.join(OUT, f"child-{pid}-{mode}.txt")
    with open(path, "w") as f:
        f.write(f"pid {pid} mode {mode} age {age:.1f}s sampled {now()}\n\n## thread names (/proc/{pid}/task/*/comm)\n")
        try:
            for t in sorted(os.listdir(f"/proc/{pid}/task"), key=int):
                f.write(f"{t} {open(f'/proc/{pid}/task/{t}/comm').read().strip()}\n")
        except FileNotFoundError:
            f.write("gone\n"); return
        try:
            gdb.execute(f"attach {pid}", to_string=True)
            for c in ("info sharedlibrary", "info threads", "thread apply all bt"):
                f.write(f"\n## {c}\n")
                f.write(gdb.execute(c, to_string=True))
            gdb.execute("detach", to_string=True)
        except gdb.error as e:
            f.write(f"\ngdb error: {e}\n")
    log(f"sampled {pid} ({mode}) at {age:.1f}s -> {os.path.basename(path)}")

sampled = set()
while probe.poll() is None:
    up = float(open("/proc/uptime").read().split()[0])
    for pid in descendants(probe.pid):
        a = argv(pid)
        if len(a) > 1 and a[1] == b"child" and pid not in sampled:
            try:
                age = up - stat(pid)[1] / CLK
            except FileNotFoundError:
                continue
            if age >= SAMPLE_AGE:
                sampled.add(pid)
                sample(pid, age)
    time.sleep(2)
log(f"probe exited {probe.returncode}")
