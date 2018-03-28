using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ZLR.Interfaces.SystemConsole;
using ZLR.VM;

namespace TestSuite
{
    abstract class TestConsoleIO : ConsoleIO, ITestCaseIO
    {
        protected string inputFile;

        private Native.CHAR_INFO[,] savedConsole;
        private int savedX, savedY, savedW, savedH;
        private string savedTitle;

        public TestConsoleIO(string inputFile)
            : base("CONSOLE_TEST_CASE")
        {
            this.inputFile = inputFile;
            this.HideMorePrompts = true;
        }

        private static class Native
        {
            // thanks, pinvoke.net

            internal const int STD_OUTPUT_HANDLE = -11;

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr GetStdHandle(int nStdHandle);

            [DllImport("kernel32.dll")]
            internal static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput,
                out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

            [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool ReadConsoleOutput(
                IntPtr hConsoleOutput,
                [MarshalAs(UnmanagedType.LPArray), Out] CHAR_INFO[,] lpBuffer,
                COORD dwBufferSize,
                COORD dwBufferCoord,
                ref SMALL_RECT lpReadRegion);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool WriteConsoleOutput(
                IntPtr hConsoleOutput,
                [MarshalAs(UnmanagedType.LPArray), In] CHAR_INFO[,] lpBuffer,
                COORD dwBufferSize,
                COORD dwBufferCoord,
                ref SMALL_RECT lpWriteRegion
                );

            [StructLayout(LayoutKind.Explicit)]
            internal struct CHAR_INFO
            {
                [FieldOffset(0)]
                public char UnicodeChar;
                [FieldOffset(0)]
                public char AsciiChar;
                [FieldOffset(2)]
                public UInt16 Attributes;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct COORD
            {
                public short X;
                public short Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct SMALL_RECT
            {
                public short Left;
                public short Top;
                public short Right;
                public short Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct CONSOLE_SCREEN_BUFFER_INFO
            {
                public COORD dwSize;
                public COORD dwCursorPosition;
                public short wAttributes;
                public SMALL_RECT srWindow;
                public COORD dwMaximumWindowSize;
            }
        }

        private static Native.CHAR_INFO[,] GetConsoleContents()
        {
            var hStdout = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
            var height = Console.WindowHeight;
            var width = Console.WindowWidth;

            Native.CONSOLE_SCREEN_BUFFER_INFO bufferInfo;
            Native.GetConsoleScreenBufferInfo(hStdout, out bufferInfo);

            var buffer = new Native.CHAR_INFO[height, width];

            var bufferSize = new Native.COORD { X = (short)width, Y = (short)height };
            var bufferOffset = new Native.COORD();
            var readRegion = bufferInfo.srWindow;

            if (Native.ReadConsoleOutput(hStdout, buffer, bufferSize, bufferOffset, ref readRegion))
            {
                return buffer;
            }
            else
            {
                throw new Exception("Can't read console output");
            }
        }

        private static void SetConsoleContents(Native.CHAR_INFO[,] buffer)
        {
            var hStdout = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
            var height = Console.WindowHeight;
            var width = Console.WindowWidth;

            Native.CONSOLE_SCREEN_BUFFER_INFO bufferInfo;
            Native.GetConsoleScreenBufferInfo(hStdout, out bufferInfo);

            var bufferSize = new Native.COORD { X = (short)width, Y = (short)height };
            var bufferOffset = new Native.COORD();
            var writeRegion = bufferInfo.srWindow;

            if (!Native.WriteConsoleOutput(hStdout, buffer, bufferSize, bufferOffset, ref writeRegion))
            {
                throw new Exception("Can't write console output");
            }
        }

        public void BeforeRunning()
        {
            savedW = Console.WindowWidth;
            savedH = Console.WindowHeight;
            savedConsole = GetConsoleContents();
            savedX = Console.CursorLeft;
            savedY = Console.CursorTop;
            savedTitle = Console.Title;
        }

        public void AfterRunning()
        {
            Console.SetWindowSize(savedW, savedH);
            SetConsoleContents(savedConsole);
            Console.SetCursorPosition(
                Math.Min(savedX, Console.BufferWidth - 1),
                Math.Min(savedY, Console.BufferHeight - 1));
            Console.Title = savedTitle;
        }

        public string CollectOutput()
        {
            var buffer = GetConsoleContents();
            var sb = new StringBuilder(Console.WindowWidth * Console.WindowHeight);

            for (var row = 0; row < buffer.GetLength(0); row++)
            {
                for (var col = 0; col < buffer.GetLength(1); col++)
                {
                    sb.Append(buffer[row, col].UnicodeChar);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        void IZMachineIO.PutCommand(string text)
        {
            // nada
        }
    }

    class ReplayGridIO : TestConsoleIO, IZMachineIO
    {
        public ReplayGridIO(string filename)
            : base(filename)
        {
        }

        Stream IZMachineIO.OpenCommandFile(bool writing)
        {
            if (writing)
                return null;

            return new FileStream(inputFile, FileMode.Open, FileAccess.Read);
        }
    }

    class RecordingGridIO : TestConsoleIO, IZMachineIO
    {
        public RecordingGridIO(string filename)
            : base(filename)
        {
        }

        Stream IZMachineIO.OpenCommandFile(bool writing)
        {
            if (!writing)
                return null;

            return new FileStream(inputFile, FileMode.Create, FileAccess.Write);
        }
    }
}
