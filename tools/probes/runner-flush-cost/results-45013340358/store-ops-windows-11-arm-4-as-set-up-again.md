### Windows arm64: as the runner is set up, again

Microsoft Windows 10.0.26200, Arm64, 4 cores, .NET 10.0.15. Temporary directory C:\Users\runneradmin\AppData\Local\Temp\ (NTFS, 124 GB free). All operations: 32 s.

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 11.70 | 13.67 | 50 |
| file: create and delete an empty file | 0.27 | 0.30 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 4.77 | 5.86 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 4.83 | 5.64 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 1.51 | 1.71 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 3.14 | 3.42 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 1.50 | 1.71 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 4.86 | 5.69 | 100 |
| store: create and close (ForTesting) | 100.89 | 114.77 | 15 |
| store: one insert (AddCurrent) | 24.82 | 30.62 | 100 |
| store: a batch of 100 inserts | 1331.85 | 1401.53 | 10 |
| store: SPARQL INSERT DATA of one quad | 30.59 | 33.50 | 50 |
| store: SPARQL SELECT of one subject | 0.02 | 0.02 | 200 |
| store: checkpoint | 20.50 | 22.62 | 10 |
| store: reopen and close (10 writes in its log) | 38.17 | 62.78 | 10 |
| store: delete a closed store's directory | 1.53 | 1.66 | 15 |
| pool: rent a used store (it is cleared), insert, return | 66.03 | 117.18 | 20 |

