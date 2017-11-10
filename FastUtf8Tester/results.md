# Perf results

50,000 iterations on each text

Testbed: Intel(R) Core(TM) i7-6700 CPU @ 3.40GHz, 3408 Mhz, 8 Core(s), 8 Logical Processor(s), running AMD64 optimized

Also tested various offsets so that input and output buffers weren't aligned, but didn't seem to affect results measurably.

## English (ASCII)

167,546 UTF-8 bytes => 167,546 UTF-16 chars

### Convert UTF-8 to UTF-16

* in-box decoder: 2.88 sec
* new decoder: 0.61 sec

79% reduction in runtime

### Get UTF-16 char count from UTF-8 input

* in-box decoder: 0.60 sec
* new decoder: 0.13 sec

78% reduction in runtime

## English (UTF8, some 3-byte chars)

173,592 UTF-8 bytes => 167,552 UTF-16 chars

### Convert UTF-8 to UTF-16

* in-box decoder: 5.24 sec
* new decoder: 5.16 sec

1% reduction in runtime

### Get UTF-16 char count from UTF-8 input

* in-box decoder: 3.39 sec
* new decoder: 2.36 sec

30% reduction in runtime

## Cyrillic (mostly 2-byte chars)

102,245 UTF-8 bytes => 66,808 UTF-16 chars

### Convert UTF-8 to UTF-16

* in-box decoder: 6.81 sec
* new decoder: 5.55 sec

18% reduction in runtime

### Get UTF-16 char count from UTF-8 input

* in-box decoder: 6.60 sec
* new decoder: 4.14 sec

37% reduction in runtime

## Greek (mostly 2-byte chars)

131,761 UTF-8 bytes => 84,663 UTF-16 chars

### Convert UTF-8 to UTF-16

* in-box decoder: 10.38 sec
* new decoder: 8.88 sec

14% reduction in runtime

### Get UTF-16 char count from UTF-8 input

* in-box decoder: 10.13 sec
* new decoder: 7.16 sec

30% reduction in runtime

## Chinese (mostly 3-byte chars)

180,651 UTF-8 bytes => 77,967 UTF-16 chars

### Convert UTF-8 to UTF-16

* in-box decoder: 9.05 sec
* new decoder: 6.87 sec

24% reduction in runtime

### Get UTF-16 char count from UTF-8 input

* in-box decoder: 7.15 sec
* new decoder: 3.78 sec

47% reduction in runtime
