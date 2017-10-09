using System;
using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace FastUtf8Tester
{
    class Program
    {
        private const int BATT_COUNT = 5;
        private const int NUM_ITERS = 1_000_000;

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
                Console.WriteLine("1. English");
                Console.WriteLine("2. Hebrew (2-byte chars)");
                Console.WriteLine("3. Chinese (3-byte chars)");
                Console.WriteLine("4. Russian (2-byte chars)");
                Console.WriteLine("5. Japanese (3-byte chars)");
                Console.Write("? ");

                string lipsum;
                switch (Int32.Parse(Console.ReadLine()))
                {
                    case 1: { lipsum = Lipsum.English; break; }
                    case 2: { lipsum = Lipsum.Hebrew; break; }
                    case 3: { lipsum = Lipsum.Chinese; break; }
                    case 4: { lipsum = Lipsum.Russian; break; }
                    case 5: { lipsum = Lipsum.Japanese; break; }
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

            Encoding.UTF8.GetString(asBytes); // don't use return value, simply to ensure method is JITted

            var stopwatch = new Stopwatch();

            for (int i = 0; i < BATT_COUNT; i++)
            {
                stopwatch.Restart();
                for (int j = 0; j < NUM_ITERS; j++)
                {
                    Encoding.UTF8.GetChars(asBytes, asChars);
                }
                Console.WriteLine($"Elapsed: {stopwatch.Elapsed}");
            }
        }

        private static void TestUtf8Util(string lipsum)
        {
            byte[] asBytes = Encoding.UTF8.GetBytes(lipsum);
            char[] asChars = new char[Encoding.UTF8.GetCharCount(asBytes)];

            Utf8Util.ConvertUtf8ToUtf16(asBytes, asChars);
            if (new String(asChars) != lipsum)
            {
                throw new Exception("Didn't decode properly!");
            }
            Array.Clear(asChars, 0, asChars.Length);

            var stopwatch = new Stopwatch();

            for (int i = 0; i < BATT_COUNT; i++)
            {
                stopwatch.Restart();
                for (int j = 0; j < NUM_ITERS; j++)
                {
                    Utf8Util.ConvertUtf8ToUtf16(asBytes, asChars);
                }
                Console.WriteLine($"Elapsed: {stopwatch.Elapsed}");
            }
        }
    }
}
