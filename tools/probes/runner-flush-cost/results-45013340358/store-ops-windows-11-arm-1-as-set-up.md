### Windows arm64: as the runner is set up

Microsoft Windows 10.0.26200, Arm64, 4 cores, .NET 10.0.15. Temporary directory C:\Users\runneradmin\AppData\Local\Temp\ (NTFS, 124 GB free). All operations: 35 s.

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 12.70 | 30.31 | 50 |
| file: create and delete an empty file | 0.27 | 0.30 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 4.96 | 8.56 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 4.89 | 6.13 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 1.56 | 1.84 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 3.29 | 3.68 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 1.57 | 1.87 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 4.98 | 5.88 | 100 |
| store: create and close (ForTesting) | 107.39 | 124.90 | 15 |
| store: one insert (AddCurrent) | 25.54 | 30.44 | 100 |
| store: a batch of 100 inserts | 1317.29 | 1408.25 | 10 |
| store: SPARQL INSERT DATA of one quad | 31.10 | 35.93 | 50 |
| store: SPARQL SELECT of one subject | 0.02 | 0.02 | 200 |
| store: checkpoint | 20.30 | 22.75 | 10 |
| store: reopen and close (10 writes in its log) | 24.54 | 75.47 | 10 |
| store: delete a closed store's directory | 1.41 | 1.59 | 15 |
| pool: rent a used store (it is cleared), insert, return | 81.26 | 151.94 | 20 |

