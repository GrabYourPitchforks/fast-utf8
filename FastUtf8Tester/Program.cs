using System;
using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace FastUtf8Tester
{
    class Program
    {
        private const int BATT_COUNT = 5;
        private const int NUM_ITERS = 10_000;

        static void Main(string[] args)
        {
            Console.WriteLine("TESTBENCH");
            Console.WriteLine($"64-bit OS = {Environment.Is64BitOperatingSystem}");
            Console.WriteLine($"64-bit process = {Environment.Is64BitProcess}");
            Console.WriteLine($"Hardware vector acceleration = {Vector.IsHardwareAccelerated}");
            Console.WriteLine();

            while (true)
            {
                Console.WriteLine("Select text:");
                Console.WriteLine("1. English (ASCII)");
                Console.WriteLine("2. English (UTF8)");
                Console.WriteLine("3. Russian (2-byte chars)");
                Console.WriteLine("4. Greek (2-byte chars)");
                Console.WriteLine("5. Chinese (3-byte chars)");
                Console.Write("? ");

                string lipsum;
                switch (Int32.Parse(Console.ReadLine()))
                {
                    case 1: { lipsum = SampleTexts.English_Ascii; break; }
                    case 2: { lipsum = SampleTexts.English_Utf8; break; }
                    case 3: { lipsum = SampleTexts.Russian; break; }
                    case 4: { lipsum = SampleTexts.Greek; break; }
                    case 5: { lipsum = SampleTexts.Chinese; break; }
                    default: { return; }
                }
                Console.WriteLine();

                Console.WriteLine("Select decoder:");
                Console.WriteLine("1. System.Text.Encoding.UTF8");
                Console.WriteLine("2. Prototype UTF8 decoder");
                Console.Write("? ");

                Action<string> decode;
                switch (Int32.Parse(Console.ReadLine()))
                {
                    case 1: { decode = TestSystemTextEncoding; break; }
                    case 2: { decode = TestUtf8Util; break; }
                    default: { return; }
                }
                Console.WriteLine();

                Console.WriteLine("BEGIN TEST");
                decode(lipsum);
                Console.WriteLine("END TEST");
                Console.WriteLine();
            }
        }

        private static void TestSystemTextEncoding(string lipsum)
        {
            byte[] asBytes = Encoding.UTF8.GetBytes(lipsum);
            char[] asChars = new char[Encoding.UTF8.GetCharCount(asBytes)];

            Console.WriteLine(Encoding.UTF8.GetCharCount(asBytes));
            Encoding.UTF8.GetString(asBytes); // don't use return value, simply to ensure method is JITted

            var stopwatch = new Stopwatch();

            for (int i = 0; i < BATT_COUNT; i++)
            {
                stopwatch.Restart();
                for (int j = 0; j < NUM_ITERS; j++)
                {
                    Encoding.UTF8.GetCharCount(asBytes);
                }
                Console.WriteLine($"Elapsed: {stopwatch.Elapsed}");
            }
        }

        private static void TestUtf8Util(string lipsum)
        {
            byte[] asBytes = Encoding.UTF8.GetBytes(lipsum);
            char[] asChars = new char[Encoding.UTF8.GetCharCount(asBytes)];

            Console.WriteLine(Utf8Util.GetUtf16CharCount(asBytes));
             Utf8Util.ConvertUtf8ToUtf16(asBytes, asChars);
            if (new String(asChars) != lipsum)
            {
            //    throw new Exception("Didn't decode properly!");
            }
            Array.Clear(asChars, 0, asChars.Length);

            var stopwatch = new Stopwatch();

            for (int i = 0; i < BATT_COUNT; i++)
            {
                stopwatch.Restart();
                for (int j = 0; j < NUM_ITERS; j++)
                {
                    Utf8Util.GetUtf16CharCount(asBytes);
                }
                Console.WriteLine($"Elapsed: {stopwatch.Elapsed}");
            }
        }
    }
}
