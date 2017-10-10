# Perf results

1,000,000 iterations on each lipsum

Testbed: Intel(R) Core(TM) i7-6700 CPU @ 3.40GHz, 3408 Mhz, 8 Core(s), 8 Logical Processor(s), running AMD64 optimized

Also tested various offsets so that input and output buffers weren't aligned, but didn't seem to affect results measurably.

## English

(3821 ASCII chars)

### UTF8 to UTF16

* `System.Text.Encoding.UTF8`: 1.22 sec
* `Encodings.Utf8.ToUtf16`: 1.21 sec
* `Utf8Util.ConvertUtf8ToUtf16`: 0.21 sec

83% reduction in runtime

### Get UTF16 char count from UTF8

* `System.Text.Encoding.UTF8`: 0.286 sec
* `Utf8Util.GetCharCount`: 0.077 sec

73% reduction in runtime

## Hebrew

(5428 bytes = 3051 chars)

### UTF8 to UTF16

* `System.Text.Encoding.UTF8`: 5.13 sec
* `Encodings.Utf8.ToUtf16`: 5.87 sec
* `Utf8Util.ConvertUtf8ToUtf16`: 3.56 sec

30% - 40% reduction in runtime

### Get UTF16 char count from UTF8

* `System.Text.Encoding.UTF8`: 5.22 sec
* `Utf8Util.GetCharCount`: 1.91 sec

63% reduction in runtime

## Cyrillic

(5402 byte = 3041 chars)

### UTF8 to UTF16

* `System.Text.Encoding.UTF8`: 4.84 sec
* `Encodings.Utf8.ToUtf16`: 4.98 sec
* `Utf8Util.ConvertUtf8ToUtf16`: 3.70 sec

25% reduction in runtime

### Get UTF16 char count from UTF8

* `System.Text.Encoding.UTF8`: 5.20 sec
* `Utf8Util.GetCharCount`: 1.96 sec

62% reduction in runtime

## Japanese

(4685 bytes = 1943 chars)

### UTF8 to UTF16

* `System.Text.Encoding.UTF8`: 4.30 sec
* `Encodings.Utf8.ToUtf16`: 4.22 sec
* `Utf8Util.ConvertUtf8ToUtf16`: 3.39 sec

20% reduction in runtime

### Get UTF16 char count from UTF8

* `System.Text.Encoding.UTF8`: 3.52 sec
* `Utf8Util.GetCharCount`: 1.80 sec

48% reduction in runtime

## Chinese

(8252 bytes = 3624 chars)

### UTF8 to UTF16

* `System.Text.Encoding.UTF8`: 9.76 sec
* `Encodings.Utf8.ToUtf16`: 9.57 sec
* `Utf8Util.ConvertUtf8ToUtf16`: 6.55 sec

33% reduction in runtime

### Get UTF16 char count from UTF8

* `System.Text.Encoding.UTF8`: 8.66 sec
* `Utf8Util.GetCharCount`: 3.47 sec

60% reduction in runtime
