# mercuryd

One Mercury store, served by one process until it is stopped, as a service on Linux, macOS and Windows
([ADR-065](https://github.com/bemafred/sky-omega/blob/main/docs/adrs/mercury/ADR-065-mercuryd.md)): SPARQL over HTTP
at `http://<host>:<port>/sparql` (updates at `/sparql/update`, unless `--read-only`), and the REPL at the store's
attach socket. `mercuryd --help` lists the options.

```bash
mercuryd --store shared --profile Graph --listen 100.67.254.98 --port 3032
```
