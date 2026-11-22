### Linux x64: as the runner is set up, again

Ubuntu 24.04.5 LTS, X64, 4 cores, .NET 10.0.15. Temporary directory /tmp/ (ext4, 84 GB free). All operations: 2 s.

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 0.37 | 0.45 | 50 |
| file: create and delete an empty file | 0.01 | 0.02 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 0.25 | 0.27 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 0.17 | 0.50 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 0.11 | 0.15 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 0.16 | 0.51 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 0.11 | 0.12 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 0.20 | 0.49 | 100 |
| store: create and close (ForTesting) | 5.55 | 6.63 | 15 |
| store: one insert (AddCurrent) | 1.44 | 2.06 | 100 |
| store: a batch of 100 inserts | 59.07 | 75.63 | 10 |
| store: SPARQL INSERT DATA of one quad | 1.22 | 1.34 | 50 |
| store: SPARQL SELECT of one subject | 0.01 | 0.02 | 200 |
| store: checkpoint | 1.48 | 1.63 | 10 |
| store: reopen and close (10 writes in its log) | 1.16 | 1.30 | 10 |
| store: delete a closed store's directory | 1.30 | 1.48 | 15 |
| pool: rent a used store (it is cleared), insert, return | 9.20 | 10.14 | 20 |

