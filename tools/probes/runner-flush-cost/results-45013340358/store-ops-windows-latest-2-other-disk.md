### Windows x64: the temporary directory on D:\

Microsoft Windows 10.0.26100, X64, 4 cores, .NET 10.0.15. Temporary directory D:\mops-temp\ (NTFS, 146 GB free). All operations: 2 s.

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 0.76 | 0.97 | 50 |
| file: create and delete an empty file | 0.14 | 0.17 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 0.32 | 0.45 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 0.27 | 0.40 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 0.08 | 0.11 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 0.18 | 0.30 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 0.08 | 0.12 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 0.35 | 0.47 | 100 |
| store: create and close (ForTesting) | 7.92 | 9.09 | 15 |
| store: one insert (AddCurrent) | 1.56 | 2.05 | 100 |
| store: a batch of 100 inserts | 78.15 | 90.50 | 10 |
| store: SPARQL INSERT DATA of one quad | 2.27 | 2.52 | 50 |
| store: SPARQL SELECT of one subject | 0.02 | 0.03 | 200 |
| store: checkpoint | 1.90 | 2.00 | 10 |
| store: reopen and close (10 writes in its log) | 3.83 | 4.22 | 10 |
| store: delete a closed store's directory | 1.34 | 1.53 | 15 |
| pool: rent a used store (it is cleared), insert, return | 5.44 | 5.86 | 20 |

