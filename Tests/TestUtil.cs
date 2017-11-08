using System;

namespace Tests
{
    public static class TestUtil
    {
        public static byte[] DecodeHex(string input)
        {
            int ParseNibble(char ch)
            {
                ch -= (char)'0';
                if (ch < 10) { return ch; }

                ch -= (char)('A' - '0');
                if (ch < 6) { return (ch + 10); }

                ch -= (char)('a' - 'A');
                if (ch < 6) { return (ch + 10); }

                throw new Exception("Invalid hex character.");
            }

            if (input.Length % 2 != 0) { throw new Exception("Invalid hex data."); }

            byte[] retVal = new byte[input.Length / 2];
            for (int i = 0; i < retVal.Length; i++)
            {
                retVal[i] = (byte)((ParseNibble(input[2 * i]) << 4) | ParseNibble(input[2 * i + 1]));
            }

            return retVal;
        }
    }
}
