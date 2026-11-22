### Windows x64: as the runner is set up, again

Microsoft Windows 10.0.26100, X64, 4 cores, .NET 10.0.15. Temporary directory C:\Users\runneradmin\AppData\Local\Temp\ (NTFS, 29 GB free). All operations: 22 s.

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 9.14 | 11.34 | 50 |
| file: create and delete an empty file | 0.39 | 0.43 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 3.70 | 5.62 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 3.45 | 4.25 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 1.09 | 1.34 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 2.42 | 2.94 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 1.07 | 1.23 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 3.54 | 4.43 | 100 |
| store: create and close (ForTesting) | 77.32 | 85.98 | 15 |
| store: one insert (AddCurrent) | 17.82 | 22.30 | 100 |
| store: a batch of 100 inserts | 963.68 | 1002.01 | 10 |
| store: SPARQL INSERT DATA of one quad | 19.97 | 22.35 | 50 |
| store: SPARQL SELECT of one subject | 0.02 | 0.03 | 200 |
| store: checkpoint | 12.73 | 16.31 | 10 |
| store: reopen and close (10 writes in its log) | 16.69 | 37.44 | 10 |
| store: delete a closed store's directory | 1.89 | 1.93 | 15 |
| pool: rent a used store (it is cleared), insert, return | 55.48 | 63.52 | 20 |

