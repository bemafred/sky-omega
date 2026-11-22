### Linux x64: as the runner is set up

Ubuntu 24.04.5 LTS, X64, 4 cores, .NET 10.0.15. Temporary directory /tmp/ (ext4, 84 GB free). All operations: 2 s.

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 0.49 | 0.61 | 50 |
| file: create and delete an empty file | 0.01 | 0.01 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 0.33 | 0.41 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 0.18 | 0.63 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 0.11 | 0.14 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 0.19 | 0.69 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 0.12 | 0.14 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 0.35 | 0.71 | 100 |
| store: create and close (ForTesting) | 6.96 | 10.18 | 15 |
| store: one insert (AddCurrent) | 1.71 | 1.85 | 100 |
| store: a batch of 100 inserts | 67.08 | 81.93 | 10 |
| store: SPARQL INSERT DATA of one quad | 1.43 | 1.55 | 50 |
| store: SPARQL SELECT of one subject | 0.02 | 0.02 | 200 |
| store: checkpoint | 1.59 | 1.70 | 10 |
| store: reopen and close (10 writes in its log) | 1.08 | 1.37 | 10 |
| store: delete a closed store's directory | 1.28 | 1.39 | 15 |
| pool: rent a used store (it is cleared), insert, return | 8.96 | 10.03 | 20 |

