# Operational runbook: run a check on one of our test machines

**Status:** In force since 2026-11-15 (Martin's decision)
**Audience:** Anyone, person or agent, who runs code on zbook, elite, omega-ubuntu or omega-windows from another machine

The machines are listed in the [README](../../README.md#platforms). None of them does work of its own: whatever
state a checkout there is in, one of us left it.

## The rule

**A test machine gets commits, never patches.**

- Its repository checkout is at a commit that has a hash, and it has no uncommitted change.
- A check runs on a named commit, and its result is reported with that hash.
- Do not copy a patch to a machine and apply it (`git diff > x.patch`, `git apply x.patch`).

## Why

On 2026-11-07 a patch was applied to the checkout on omega-windows for a DrHook check and never reversed. The
checkout stayed at an old commit with 29 changed and untracked files for eight days. On 2026-11-15 another check
found it and could not use the checkout until every file had been compared with what was pushed.

Two things are wrong with a patch:

- **It needs a second step that is forgotten.** A patch has to be reversed (`git apply -R`) before the checkout can
  be used again. On the same day, three patches were applied and reversed on zbook, and the one on omega-windows was
  not.
- **It does not name what ran.** A patch can apply cleanly onto a commit other than the one it was made from, and
  the result is a tree that nobody tested anywhere else. A commit names its whole tree.

## Before anything else: look

```bash
ssh <machine>
cd ~/src/repos/sky-omega        # omega-windows: C:\Users\bemafred\src\repos\sky-omega
git status -sb
git log --oneline -1
```

If the checkout is not clean, do not build on it and do not reset it yet. Find out what the changes are: compare
each changed or untracked file with the pushed versions (`git hash-object <file>` against
`git rev-parse <commit>:<file>`). Reset the checkout only when every file is shown to be a pushed version, or when
the one who left it says so.

## Work that is pushed

```bash
git fetch origin
git checkout --detach <hash>
# build, run, read the result
git checkout main
git pull --ff-only
```

## Work that is not pushed

Carry the commits themselves, as a bundle. On the machine that has them:

```bash
git bundle create /tmp/work.bundle origin/main..HEAD
git rev-parse HEAD                      # the hash the check will run on
scp /tmp/work.bundle <machine>:<its scratch directory>/
```

On the test machine:

```bash
git fetch origin                        # the bundle starts from origin/main, so the machine must have it
git fetch <scratch directory>/work.bundle HEAD
git checkout --detach FETCH_HEAD
git rev-parse HEAD                      # must be the hash from above
# build, run, read the result
git checkout main
```

The bundle is made from `origin/main`, so the machine needs `origin/main` as it is on the machine that made it. If
the two differ, the fetch of the bundle fails and says which commit is missing, which is the right outcome.

## Prefer a separate clone for a check

A check that needs a build of its own goes in a separate clone in the machine's scratch directory. The machine's
main checkout then stays at `origin/main`, and a cleanup that is forgotten leaves a stray directory and not a wrong
checkout.

```bash
git clone --depth 1 --recurse-submodules --shallow-submodules https://github.com/bemafred/sky-omega.git <scratch directory>/<name>
cd <scratch directory>/<name>
git fetch --depth 1 origin <full hash> && git checkout --detach FETCH_HEAD
```

The fetch by hash needs all forty characters of it; a short hash is refused as an unknown ref.

| Machine | Repository checkout | Scratch directory |
|---------|---------------------|-------------------|
| zbook, elite, omega-ubuntu | `~/src/repos/sky-omega` | `~/diag` |
| omega-windows | `C:\Users\bemafred\src\repos\sky-omega` | `C:\Users\bemafred\diag` |

## Afterwards

- Report the result with the commit's hash, the machine, and where its log is on that machine.
- Leave the main checkout on `main`, clean. `git status -sb` shows it.
- Remove a scratch clone when its check is done, or say in the report that it is still there.

## Notes for each machine

- **omega-windows.** The shell over ssh is `cmd`. Put PowerShell in a `.ps1` file, copy it with `scp`, and run it
  with `pwsh -NoProfile -File`; a one-line command chain is mangled. Let the script write its own log.
- **omega-ubuntu and omega-windows** are virtual machines on omega. A long run on one of them loads omega, and
  tests on omega that depend on timing can fail meanwhile.
- **Linux machines.** Use Microsoft's .NET build ([Linux .NET runbook](runbook-linux-dotnet-runtime.md)).
