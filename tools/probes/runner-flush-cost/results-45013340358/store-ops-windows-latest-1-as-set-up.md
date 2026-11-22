### Windows x64: as the runner is set up

Microsoft Windows 10.0.26100, X64, 4 cores, .NET 10.0.15. Temporary directory C:\Users\runneradmin\AppData\Local\Temp\ (NTFS, 29 GB free). All operations: 23 s.

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 17.87 | 35.07 | 50 |
| file: create and delete an empty file | 0.52 | 0.59 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 3.30 | 5.56 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 3.09 | 3.56 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 0.91 | 1.02 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 2.09 | 2.40 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 0.97 | 1.21 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 3.26 | 4.43 | 100 |
| store: create and close (ForTesting) | 70.11 | 90.75 | 15 |
| store: one insert (AddCurrent) | 16.51 | 20.33 | 100 |
| store: a batch of 100 inserts | 881.33 | 937.95 | 10 |
| store: SPARQL INSERT DATA of one quad | 21.20 | 25.14 | 50 |
| store: SPARQL SELECT of one subject | 0.02 | 0.03 | 200 |
| store: checkpoint | 14.51 | 16.51 | 10 |
| store: reopen and close (10 writes in its log) | 17.47 | 31.02 | 10 |
| store: delete a closed store's directory | 1.89 | 2.07 | 15 |
| pool: rent a used store (it is cleared), insert, return | 49.47 | 58.87 | 20 |

