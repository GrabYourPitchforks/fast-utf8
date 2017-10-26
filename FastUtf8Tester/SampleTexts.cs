using System;
using System.IO;

namespace FastUtf8Tester
{
    internal static class SampleTexts
    {
        public static readonly string English_Ascii = File.ReadAllText(@".\SampleTexts\11.txt");
        public static readonly string English_Utf8 = File.ReadAllText(@".\SampleTexts\11-0.txt");
        public static readonly string Russian = File.ReadAllText(@".\SampleTexts\30774-0.txt");
        public static readonly string Greek = File.ReadAllText(@".\SampleTexts\39251-0.txt");
        public static readonly string Chinese = File.ReadAllText(@".\SampleTexts\25249-0.txt");
    }
}
