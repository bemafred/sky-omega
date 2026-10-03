// Finding 87 diagnostic — DYLD interposer that logs every waitpid / wait4 / waitid call with the
// caller's image (dladdr), pid, options, result and errno. It identified the second reaper (the CoreCLR
// PAL in the in-process debug components) competing with the BCL for a Process.Start child.
//
//   clang -dynamiclib -o libwpi.dylib 87-wait-interposer.c
//   WPI_LOG=/path/wpi.log DYLD_INSERT_LIBRARIES=/Users/bemafred/src/repos/sky-omega/libwpi.dylib <non-hardened apphost> ...
//
// lldb could not be used here (hung at process launch); interposition needs no debugger rights.
#include <sys/wait.h>
#include <sys/resource.h>
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <errno.h>
#include <string.h>
#include <unistd.h>
#include <fcntl.h>
static void wlog(const char *fn, long pid, int opt, long r, int e, void *ra) {
    Dl_info di; const char *img = "?";
    if (dladdr(ra, &di) && di.dli_fname) { img = strrchr(di.dli_fname, '/'); img = img ? img + 1 : di.dli_fname; }
    int fd = open(getenv("WPI_LOG"), O_WRONLY|O_CREAT|O_APPEND, 0644);
    char buf[256]; int n = snprintf(buf, sizeof buf, "%s host=%d pid=%ld opt=%d ret=%ld errno=%d img=%s\n", fn, getpid(), pid, opt, r, r < 0 ? e : 0, img);
    write(fd, buf, n); close(fd);
}
static pid_t my_waitpid(pid_t pid, int *st, int opt) { void *ra = __builtin_return_address(0); pid_t r = waitpid(pid, st, opt); int e = errno; wlog("waitpid", pid, opt, r, e, ra); errno = e; return r; }
static pid_t my_wait4(pid_t pid, int *st, int opt, struct rusage *ru) { void *ra = __builtin_return_address(0); pid_t r = wait4(pid, st, opt, ru); int e = errno; wlog("wait4", pid, opt, r, e, ra); errno = e; return r; }
static int my_waitid(idtype_t t, id_t id, siginfo_t *si, int opt) { void *ra = __builtin_return_address(0); int r = waitid(t, id, si, opt); int e = errno; wlog(t == P_PID ? "waitid(P_PID)" : "waitid(P_ALL)", (long)id, opt, r == 0 ? (long)si->si_pid : r, e, ra); errno = e; return r; }
__attribute__((used)) static struct { const void *n; const void *o; } interposers[] __attribute__((section("__DATA,__interpose"))) = {
  { (const void*)my_waitpid, (const void*)waitpid }, { (const void*)my_wait4, (const void*)wait4 }, { (const void*)my_waitid, (const void*)waitid } };
