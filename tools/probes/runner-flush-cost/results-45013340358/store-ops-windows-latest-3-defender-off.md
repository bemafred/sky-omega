### Windows x64: Defender's real-time protection off

Microsoft Windows 10.0.26100, X64, 4 cores, .NET 10.0.15. Temporary directory C:\Users\runneradmin\AppData\Local\Temp\ (NTFS, 29 GB free). All operations: 22 s.

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 8.36 | 11.05 | 50 |
| file: create and delete an empty file | 0.32 | 0.38 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 3.47 | 4.57 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 3.62 | 4.26 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 1.20 | 1.42 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 2.45 | 3.16 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 1.06 | 1.28 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 3.58 | 4.52 | 100 |
| store: create and close (ForTesting) | 76.41 | 85.93 | 15 |
| store: one insert (AddCurrent) | 17.93 | 21.56 | 100 |
| store: a batch of 100 inserts | 897.28 | 929.22 | 10 |
| store: SPARQL INSERT DATA of one quad | 21.26 | 24.92 | 50 |
| store: SPARQL SELECT of one subject | 0.02 | 0.03 | 200 |
| store: checkpoint | 13.32 | 14.37 | 10 |
| store: reopen and close (10 writes in its log) | 16.83 | 24.56 | 10 |
| store: delete a closed store's directory | 1.93 | 1.97 | 15 |
| pool: rent a used store (it is cleared), insert, return | 48.22 | 52.78 | 20 |

