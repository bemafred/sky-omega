### Linux x64: the temporary directory on /mnt

Ubuntu 24.04.5 LTS, X64, 4 cores, .NET 10.0.15. Temporary directory /mnt/mops-temp/ (ext4, 84 GB free). All operations: 2 s.

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 0.46 | 0.54 | 50 |
| file: create and delete an empty file | 0.01 | 0.01 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 0.31 | 0.38 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 0.17 | 0.61 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 0.11 | 0.12 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 0.18 | 0.59 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 0.12 | 0.16 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 0.36 | 0.64 | 100 |
| store: create and close (ForTesting) | 6.53 | 24.29 | 15 |
| store: one insert (AddCurrent) | 1.48 | 1.67 | 100 |
| store: a batch of 100 inserts | 57.60 | 76.88 | 10 |
| store: SPARQL INSERT DATA of one quad | 1.20 | 1.28 | 50 |
| store: SPARQL SELECT of one subject | 0.01 | 0.02 | 200 |
| store: checkpoint | 1.52 | 1.79 | 10 |
| store: reopen and close (10 writes in its log) | 1.11 | 1.42 | 10 |
| store: delete a closed store's directory | 1.37 | 1.49 | 15 |
| pool: rent a used store (it is cleared), insert, return | 9.36 | 10.33 | 20 |

