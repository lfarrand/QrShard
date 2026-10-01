# Benchmark measurements (v1.7.8 snapshot)

Measured on 2026-10-01 from v1.7.8 commit `791465d` with BenchmarkDotNet 0.16.0-preview.2,
SDK 10.0.401, and runtime 10.0.12. The machine was Ubuntu 24.04.4 LTS on an Intel Xeon
Processor at 2.40 GHz with 4 logical cores. See the README
[Benchmark snapshot](../../README.md#benchmark-snapshot).

| Size | Preset | Images | Encode | Decode | Codec MiB/s | Est. manual (3 s/img) | Est. auto (0.5 s/img) |
|---|---|---:|---:|---:|---:|---:|---:|
| 1KB | Default | 1 | 18.8 ms | 50.3 ms | 0.014 | 3.07 s | 569.2 ms |
| 1KB | Dense | 1 | 23.9 ms | 60.4 ms | 0.012 | 3.08 s | 584.2 ms |
| 1KB | Max4K | 1 | 85.2 ms | 178.2 ms | 0.004 | 3.26 s | 763.4 ms |
| 1KB | Max4K-R10 | 1+1p | 163.4 ms | 207.5 ms | 0.003 | 6.37 s | 1.37 s |
| 10KB | Default | 1 | 21.3 ms | 50.3 ms | 0.136 | 3.07 s | 571.6 ms |
| 10KB | Dense | 1 | 23.5 ms | 61.9 ms | 0.114 | 3.09 s | 585.3 ms |
| 10KB | Max4K | 1 | 71 ms | 176.4 ms | 0.039 | 3.25 s | 747.4 ms |
| 10KB | Max4K-R10 | 1+1p | 98.7 ms | 208.6 ms | 0.032 | 6.31 s | 1.31 s |
| 100KB | Default | 1 | 67.2 ms | 62.5 ms | 0.753 | 3.13 s | 629.7 ms |
| 100KB | Dense | 1 | 43.9 ms | 85.7 ms | 0.754 | 3.13 s | 629.5 ms |
| 100KB | Max4K | 1 | 82.4 ms | 185.8 ms | 0.364 | 3.27 s | 768.1 ms |
| 100KB | Max4K-R10 | 1+1p | 117.3 ms | 200.6 ms | 0.307 | 6.32 s | 1.32 s |
| 500KB | Default | 3 | 82.9 ms | 69 ms | 3.2 | 9.15 s | 1.65 s |
| 500KB | Dense | 1 | 146.5 ms | 68.3 ms | 2.3 | 3.21 s | 714.8 ms |
| 500KB | Max4K | 1 | 82.6 ms | 209.6 ms | 1.7 | 3.29 s | 792.2 ms |
| 500KB | Max4K-R10 | 1+1p | 108.7 ms | 261.5 ms | 1.3 | 6.37 s | 1.37 s |
| 1MB | Default | 5 | 164.4 ms | 89.1 ms | 3.9 | 15.25 s | 2.75 s |
| 1MB | Dense | 2 | 173.7 ms | 89.3 ms | 3.8 | 6.26 s | 1.26 s |
| 1MB | Max4K | 1 | 95 ms | 238.2 ms | 3 | 3.33 s | 833.2 ms |
| 1MB | Max4K-R10 | 1+1p | 124.9 ms | 270 ms | 2.5 | 6.39 s | 1.39 s |
| 10MB | Default | 50 | 1.03 s | 415.9 ms | 6.9 | 2.5 min | 26.44 s |
| 10MB | Dense | 15 | 671.1 ms | 273.5 ms | 10.6 | 45.94 s | 8.44 s |
| 10MB | Max4K | 3 | 155.9 ms | 253.6 ms | 24.4 | 9.41 s | 1.91 s |
| 10MB | Max4K-R10 | 3+1p | 193.9 ms | 340.6 ms | 18.7 | 12.53 s | 2.53 s |
| 100MB | Default | 495 | 9.32 s | 3.64 s | 7.7 | 25 min | 4.3 min |
| 100MB | Dense | 147 | 6.04 s | 1.86 s | 12.7 | 7.5 min | 1.4 min |
| 100MB | Max4K | 22 | 600.1 ms | 1.1 s | 58.8 | 1.1 min | 12.7 s |
| 100MB | Max4K-R10 | 22+3p | 898.2 ms | 1.25 s | 46.5 | 1.3 min | 14.65 s |
| 250MB | Default | 1238 | 23.63 s | 8.64 s | 7.7 | 1.04 h | 10.9 min |
| 250MB | Dense | 366 | 14.71 s | 4.21 s | 13.2 | 18.6 min | 3.4 min |
| 250MB | Max4K | 54 | 1.89 s | 2.35 s | 58.9 | 2.8 min | 31.24 s |
| 250MB | Max4K-R10 | 54+6p | 2.24 s | 2.63 s | 51.4 | 3.1 min | 34.87 s |
| 500MB | Default | 2475 | 45.88 s | 17.35 s | 7.9 | 2.08 h | 21.7 min |
| 500MB | Dense | 732 | 29.06 s | 8.37 s | 13.4 | 37.2 min | 6.7 min |
| 500MB | Max4K | 108 | 3.54 s | 4.84 s | 59.7 | 5.5 min | 1 min |
| 500MB | Max4K-R10 | 108+11p | 5.13 s | 5.04 s | 49.2 | 6.1 min | 1.2 min |
| 1GB | Default | 5068 | 1.6 min | 38.33 s | 7.7 | 4.26 h | 44.4 min |
| 1GB | Dense | 1499 | 1 min | 18.45 s | 12.9 | 1.27 h | 13.8 min |
| 1GB | Max4K | 220 | 9.01 s | 12.17 s | 48.3 | 11.4 min | 2.2 min |
| 1GB | Max4K-R10 | 220+22p | 10.05 s | 14.42 s | 41.9 | 12.5 min | 2.4 min |
