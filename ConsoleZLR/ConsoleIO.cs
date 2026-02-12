using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using ZLR.VM;

namespace ZLR.Interfaces.SystemConsole
{
    internal class ConsoleIO : IAsyncZMachineIO
    {
        private readonly string fileBase;
        private int split;
        private bool upper;
        private int xupper = 1, yupper = 1, xlower = 1, ylower = 1;
        private (ConsoleColor fg, ConsoleColor bg) upperColor = (ConsoleColor.Gray, ConsoleColor.Black);
        private (ConsoleColor fg, ConsoleColor bg) lowerColor = (ConsoleColor.Gray, ConsoleColor.Black);
        private bool reverse, emphasis;
        private bool scrollFromBottom;

        private const uint STYLE_FLAG = 0x80000000;
        private bool buffering = true;
        private int bufferLength;
        private readonly List<uint> buffer = [];
        private int lineCount;

        private const int MAX_COMMAND_HISTORY = 10;
        private readonly List<string> history = [];

        private readonly int origBufHeight;
        private readonly bool weakConsole;
        private int prevWinWidth, prevWinHeight;

        public ConsoleIO(string fileName)
        {
            fileBase = Path.GetFileName(fileName) ?? throw new ArgumentNullException(nameof(fileName));

            try
            {
                try
                {
                    // constrain the buffer height to something reasonable before the
                    // game has a chance to print too much, which will prevent us from
                    // shrinking the buffer later
                    origBufHeight = Console.BufferHeight;
                    Console.SetBufferSize(Console.WindowWidth, Console.WindowHeight);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // whoops, someone already printed too much, let's hope for the best
                }

                Console.BufferWidth = Console.WindowWidth;

                Console.Title = fileName + " - ConsoleZLR";
            }
            catch (NotSupportedException)
            {
                // Mono's Console class doesn't support changing some of these properties
                weakConsole = true;
            }
            catch (NotImplementedException)
            {
                // Mono's Console class doesn't support changing some of these properties
                weakConsole = true;
            }

            prevWinWidth = Console.WindowWidth;
            prevWinHeight = Console.WindowHeight;
        }

        [PublicAPI]
        public string? SuppliedCommandFile { get; set; }

        public bool HideMorePrompts { get; set; }

        public ReadLineResult ReadLine(string initial, int time, TimedInputCallback callback,
            byte[] terminatingKeys, bool allowDebuggerBreak)
        {
            FlushBuffer();
            lineCount = 0;

            var histIdx = history.Count;
            var savedEntry = string.Empty;
            var sleeps = 0;
            byte terminator;

            StringBuilder sb;
            int cursor;
            if (initial.Length == 0)
            {
                sb = new StringBuilder(20);
                cursor = 0;
            }
            else
            {
                sb = new StringBuilder(initial);
                cursor = initial.Length;
            }

            void ClearInput()
            {
                for (var i = cursor; i < sb.Length; i++)
                    Console.Write(' ');
                for (var i = 0; i < sb.Length; i++)
                    Console.Write("\x08 \x08");
                sb.Length = 0;
            }
            
            while (true)
            {
                if (time > 0)
                {
                    while (!Console.KeyAvailable)
                    {
                        Thread.Sleep(100);
                        if (Console.KeyAvailable)
                            break;

                        sleeps++;
                        if (sleeps == time)
                        {
                            sleeps = 0;
                            var cx = Console.CursorLeft;
                            var cy = Console.CursorTop;
                            if (callback())
                            {
                                return ReadLineResult.Cancelled;
                            }
                            // the game may have printed something anyway
                            if (Console.CursorLeft != cx ||
                                Console.CursorTop != cy)
                            {
                                Console.Write(sb.ToString());
                                for (var i = cursor; i < sb.Length; i++)
                                    Console.Write('\x08');
                            }
                        }
                    }
                }

                var info = Console.ReadKey(true);
                var special = ConsoleKeyToZSCII(info.Key);
                if (IsTerminator(special, terminatingKeys))
                {
                    terminator = special;
                    break;
                }

                switch (info.Key)
                {
                    case ConsoleKey.LeftArrow:
                        if (cursor > 0)
                        {
                            cursor--;
                            Console.Write('\x08');
                        }
                        break;

                    case ConsoleKey.RightArrow:
                        if (cursor < sb.Length)
                        {
                            Console.Write(sb[cursor]);
                            cursor++;
                        }
                        break;

                    case ConsoleKey.Home:
                        while (cursor > 0)
                        {
                            cursor--;
                            Console.Write('\x08');
                        }
                        break;

                    case ConsoleKey.End:
                        while (cursor < sb.Length)
                        {
                            Console.Write(sb[cursor]);
                            cursor++;
                        }
                        break;

                    case ConsoleKey.UpArrow:
                        if (histIdx > 0 && history.Count > 0)
                        {
                            if (histIdx == history.Count)
                                savedEntry = sb.ToString();

                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(' ');
                            for (var i = 0; i < sb.Length; i++)
                                Console.Write("\x08 \x08");

                            histIdx--;
                            sb.Length = 0;
                            sb.Append(history[histIdx]);
                            Console.Write(sb.ToString());
                            cursor = sb.Length;
                        }
                        break;

                    case ConsoleKey.DownArrow:
                        if (histIdx < history.Count && history.Count > 0)
                        {
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(' ');
                            for (var i = 0; i < sb.Length; i++)
                                Console.Write("\x08 \x08");

                            histIdx++;
                            sb.Length = 0;
                            sb.Append(histIdx == history.Count ? savedEntry : history[histIdx]);
                            Console.Write(sb.ToString());
                            cursor = sb.Length;
                        }
                        break;

                    case ConsoleKey.Backspace:
                        if (cursor > 0)
                        {
                            cursor--;
                            sb.Remove(cursor, 1);
                            Console.Write('\x08');
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(sb[i]);
                            Console.Write(' ');
                            for (var i = cursor; i <= sb.Length; i++)
                                Console.Write('\x08');
                        }
                        break;

                    case ConsoleKey.Delete:
                        if (cursor < sb.Length)
                        {
                            sb.Remove(cursor, 1);
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(sb[i]);
                            Console.Write(' ');
                            for (var i = cursor; i <= sb.Length; i++)
                                Console.Write('\x08');
                        }
                        break;

                    case ConsoleKey.Escape:
                        ClearInput();
                        break;

                    case ConsoleKey.B:
                        if (allowDebuggerBreak && info.Modifiers == ConsoleModifiers.Alt)
                        {
                            // debugger break
                            CheckScroll(true);
                            ClearInput();
                            return ReadLineResult.DebuggerBreak;
                        }
                        else
                        {
                            goto default;
                        }
                        
                    default:
                        if (info.KeyChar != '\0')
                        {
                            sb.Insert(cursor, info.KeyChar);
                            Console.Write(info.KeyChar);
                            cursor++;
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(sb[i]);
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write('\x08');
                        }
                        break;
                }
            }

            if (terminator == 13)
            {
                CheckScroll(true);
                Console.WriteLine();
            }

            var result = sb.ToString();

            history.Add(result);
            if (history.Count > MAX_COMMAND_HISTORY)
                history.RemoveAt(0);

            return ReadLineResult.LineEntered(result, terminator);
        }

        private static bool IsTerminator(byte key, byte[] terminatingKeys)
        {
            if (key == 13)
                return true;

            if (terminatingKeys.Length == 0)
                return false;

            if (terminatingKeys[0] == 255)
                return key >= 129 && key <= 154 || key >= 252 && key <= 254;

            return Array.IndexOf(terminatingKeys, key) >= 0;
        }

        public void PutCommand(string command)
        {
            PutString(command);
        }

        public short ReadKey(int time, TimedInputCallback callback, CharTranslator translator)
        {
            FlushBuffer();
            lineCount = 0;

            while (true)
            {
                if (time > 0)
                {
                    var sleeps = 0;
                    while (!Console.KeyAvailable)
                    {
                        Thread.Sleep(100);
                        if (Console.KeyAvailable)
                            break;

                        sleeps++;
                        if (sleeps == time)
                        {
                            sleeps = 0;
                            if (callback())
                                return 0;
                        }
                    }
                }

                var info = Console.ReadKey(true);
                short zkey = ConsoleKeyToZSCII(info.Key);
                if (zkey != 0)
                    return zkey;

                zkey = translator(info.KeyChar);
                if (zkey != 0)
                    return zkey;
            }
        }

        // ReSharper disable once CyclomaticComplexity
        private static byte ConsoleKeyToZSCII(ConsoleKey key)
        {
            // ReSharper disable once SwitchStatementMissingSomeCases
            return key switch
            {
                ConsoleKey.Delete => 8,
                ConsoleKey.Enter => 13,
                ConsoleKey.Escape => 27,
                ConsoleKey.UpArrow => 129,
                ConsoleKey.DownArrow => 130,
                ConsoleKey.LeftArrow => 131,
                ConsoleKey.RightArrow => 132,
                ConsoleKey.F1 => 133,
                ConsoleKey.F2 => 134,
                ConsoleKey.F3 => 135,
                ConsoleKey.F4 => 136,
                ConsoleKey.F5 => 137,
                ConsoleKey.F6 => 138,
                ConsoleKey.F7 => 139,
                ConsoleKey.F8 => 140,
                ConsoleKey.F9 => 141,
                ConsoleKey.F10 => 142,
                ConsoleKey.F11 => 143,
                ConsoleKey.F12 => 144,
                ConsoleKey.NumPad0 => 145,
                ConsoleKey.NumPad1 => 146,
                ConsoleKey.NumPad2 => 147,
                ConsoleKey.NumPad3 => 148,
                ConsoleKey.NumPad4 => 149,
                ConsoleKey.NumPad5 => 150,
                ConsoleKey.NumPad6 => 151,
                ConsoleKey.NumPad7 => 152,
                ConsoleKey.NumPad8 => 153,
                ConsoleKey.NumPad9 => 154,
                _ => 0,
            };
        }

        public void PutChar(char ch)
        {
            if (upper || !buffering)
            {
                CheckScroll(ch == '\n');
                Console.Write(ch);
                CheckMore();
            }
            else
                BufferedPutChar(ch);
        }

        public void PutString(string str)
        {
            if (upper || !buffering)
            {
                foreach (var ch in str)
                {
                    CheckScroll(ch == '\n');
                    Console.Write(ch);
                    CheckMore();
                }
            }
            else
            {
                foreach (var ch in str)
                    BufferedPutChar(ch);
            }
        }

        private void BufferedPutChar(char ch)
        {
            if (ch == ' ' || ch == '\n')
            {
                if (Console.CursorLeft + bufferLength >= Console.WindowWidth)
                {
                    CheckScroll(true);
                    Console.Write('\n');
                    CheckMore();
                }

                FlushBuffer();
                CheckScroll(ch == '\n');
                Console.Write(ch);
                CheckMore();
                return;
            }

            if (bufferLength == 0)
            {
                var (fg, bg) = GetConsoleColors();
                buffer.Add(STYLE_FLAG | ((uint)bg << 16) | (uint)fg);
            }
            buffer.Add(ch);
            bufferLength++;
        }

        public void PutTextRectangle(string[] lines)
        {
            FlushBuffer();

            var row = Console.CursorTop;
            var col = Console.CursorLeft;

            foreach (var line in lines)
            {
                if (row < Console.WindowHeight)
                    Console.SetCursorPosition(col, row++);

                Console.Write(line);
            }
        }

        private ref (ConsoleColor fg, ConsoleColor bg) WindowColors(bool upper)
        {
            if (upper)
                return ref upperColor;

            return ref lowerColor;
        }

        private (ConsoleColor fg, ConsoleColor bg) GetConsoleColors()
        {
            var result = WindowColors(upper);

            if (emphasis)
                result.fg = EmphasizeColor(result.fg);

            if (reverse)
                (result.fg, result.bg) = (result.bg, result.fg);

            return result;
        }

        private void SetConsoleColors()
        {
            var (fg, bg) = GetConsoleColors();
            Console.BackgroundColor = bg;
            Console.ForegroundColor = fg;
        }

        public void SetTextStyle(TextStyle style)
        {
            switch (style)
            {
                case TextStyle.Roman:
                    reverse = false;
                    emphasis = false;
                    break;

                case TextStyle.Reverse:
                    reverse = true;
                    break;

                case TextStyle.Bold:
                case TextStyle.Italic:
                    emphasis = true;
                    break;
            }

            if (upper || !buffering)
            {
                SetConsoleColors();
            }
            else
            {
                var (fg, bg) = GetConsoleColors();
                buffer.Add(STYLE_FLAG | ((uint)bg << 16) | (uint)fg);
            }
        }

        public void SplitWindow(short lines)
        {
            if (lines < 0)
                lines = 0;

            var oldSplit = split;
            split = Math.Min(lines, Console.WindowHeight);
            if (!weakConsole)
            {
                Console.BufferHeight = split == 0 ? origBufHeight : Console.WindowHeight;
            }

            SaveCursorPos();

            ylower = ylower + oldSplit - split;

            if (split == 0)
            {
                xupper = 1;
                yupper = 1;
                upper = false;
            }
            else
            {
                if (yupper > split)
                {
                    xupper = 1;
                    yupper = 1;
                }

                if (ylower <= split)
                {
                    ylower = Math.Min(split + 1, Console.WindowHeight);
                }
            }

            RestoreCursorPos();
        }

        public void SelectWindow(short num)
        {
            SaveCursorPos();

            switch (num)
            {
                case 0:
                    upper = false;
                    break;

                case 1:
                    upper = true;
                    xupper = 1;
                    yupper = 1;
                    break;

                default:
                    return;
            }

            RestoreCursorPos();
            SetConsoleColors();
        }

        private void RestoreCursorPos()
        {
            int x, y;

            if (upper)
            {
                x = xupper - 1;
                y = yupper - 1;
            }
            else
            {
                x = xlower - 1;
                y = ylower - 1 + split;
            }

            x = Math.Min(Math.Max(x, 0), Console.WindowWidth - 1);
            y = Math.Min(Math.Max(y, 0), Console.WindowHeight - 1);
            Console.SetCursorPosition(x + Console.WindowLeft, y + Console.WindowTop);
        }

        private void SaveCursorPos()
        {
            if (upper)
            {
                xupper = Console.CursorLeft - Console.WindowLeft + 1;
                yupper = Console.CursorTop - Console.WindowTop + 1;
            }
            else
            {
                xlower = Console.CursorLeft - Console.WindowLeft + 1;
                ylower = Console.CursorTop - Console.WindowTop - split + 1;
            }
        }

        public void EraseWindow(short num)
        {
            var oldReverse = reverse;
            try
            {
                reverse = false;
                SetConsoleColors();

                var (oldLeft, oldTop) = Console.GetCursorPosition();

                if (num < 1)
                {
                    buffer.Clear();
                    bufferLength = 0;
                }

                if (num < 0)
                {
                    // -1 = erase all and unsplit, moving the cursor to the top l, -2 = erase all but keep split and cursor position
                    // both select the lower window and move its cursor to the top left
                    Console.Clear();

                    if (num == -1)
                    {
                        split = 0;
                        upper = false;
                        xlower = 1;
                        ylower = scrollFromBottom ? Console.WindowHeight - split : 1;
                        RestoreCursorPos();
                        return;
                    }

                    if (num == -2)
                    {
                        Console.SetCursorPosition(oldLeft, oldTop);
                        return;
                    }
                }

                SaveCursorPos();

                if (num == 0)
                {
                    // erase lower
                    var height = Console.WindowHeight;
                    var width = Console.WindowWidth;
                    var startat = 0;

                    if (split > 0)
                    {
                        /* we have to move the upper window's contents down one line, because
                         * clearing the lower window will cause the whole console to scroll.
                         * this is flickery, but there doesn't seem to be a better way. */
                        /* actually, there is an alternative: keep the entire contents of the
                         * upper window in an offscreen buffer, then clear the entire screen
                         * and repaint the upper window. */
                        Console.MoveBufferArea(0, 0, width, split, 0, 1);
                        startat = split + 1;
                    }

                    Console.BackgroundColor = lowerColor.bg;
                    for (var i = startat; i < height; i++)
                    {
                        Console.SetCursorPosition(Console.WindowLeft, i + Console.WindowTop);
                        for (var j = 0; j < width; j++)
                            Console.Write(' ');
                    }

                    xlower = 1;
                    ylower = 1;
                }
                else if (num == 1)
                {
                    // erase upper
                    var height = split;
                    var width = Console.WindowWidth;
                    Console.BackgroundColor = upperColor.bg;
                    for (var i = 0; i < height; i++)
                    {
                        Console.SetCursorPosition(Console.WindowLeft, i + Console.WindowTop);
                        for (var j = 0; j < width; j++)
                            Console.Write(' ');
                    }

                    xupper = 1;
                    yupper = 1;
                }
            }
            finally
            {
                reverse = oldReverse;
                SetConsoleColors();
            }

            // restore cursor
            RestoreCursorPos();
        }

        public void EraseLine()
        {
            var oldReverse = reverse;
            try
            {
                reverse = false;
                SetConsoleColors();

                SaveCursorPos();

                var count = Console.WindowWidth - Console.CursorLeft;
                for (var i = 0; i < count; i++)
                    Console.Write(' ');

                RestoreCursorPos();
            }
            finally
            {
                reverse = oldReverse;
            }
        }

        public void MoveCursor(short x, short y)
        {
            // only allowed when upper window is selected
            if (upper)
            {
                if (x < 1)
                    x = 1;
                else if (x > Console.WindowWidth)
                    x = (short)Console.WindowWidth;

                if (y < 1)
                    y = 1;

                if (y > split)
                    SplitWindow(y);

                Console.SetCursorPosition(x - 1 + Console.WindowLeft, y - 1 + Console.WindowTop);
            }
        }

        public void GetCursorPos(out short x, out short y)
        {
            if (!upper)
                FlushBuffer();

            var cx = Console.CursorLeft - Console.WindowLeft;
            var cy = Console.CursorTop - Console.WindowTop;

            if (upper)
            {
                x = (short)(cx + 1);
                y = (short)(cy + 1);
            }
            else
            {
                x = (short)(cx + 1);
                y = (short)(cy + 1 - split);
            }
        }

        public bool ForceFixedPitch
        {
            get => true;
            set { /* nada */ }
        }

        public bool BoldAvailable => true;

        public bool ItalicAvailable => true;

        public bool FixedPitchAvailable => false;

        public bool VariablePitchAvailable => false;

        public bool ScrollFromBottom
        {
            get => scrollFromBottom;

            set
            {
                if (scrollFromBottom != value)
                {
                    scrollFromBottom = value;

                    SaveCursorPos();

                    if (value)
                    {
                        if (xlower == 1 && ylower == 1)
                            ylower = Console.WindowHeight - split;
                    }
                    else
                    {
                        if (xlower == 1 && ylower == Console.WindowHeight - split)
                            ylower = 1;
                    }

                    RestoreCursorPos();
                }
            }
        }


        public bool TimedInputAvailable => true;

        public bool Transcripting
        {
            get => false;
            set { /* nada */}
        }

        public void PutTranscriptChar(char ch)
        {
            // nada
        }

        public void PutTranscriptString(string str)
        {
            // nada
        }

        public void SetColors(short fg, short bg)
        {
            ref var winColors = ref WindowColors(upper);

            winColors.fg = ColorToConsole(fg, winColors.fg, false);
            winColors.bg = ColorToConsole(bg, winColors.bg, true);

            SetConsoleColors();
        }

        /*
            0  =  the current setting of this colour
            1  =  the default setting of this colour
            2  =  black   3 = red       4 = green    5 = yellow
            6  =  blue    7 = magenta   8 = cyan     9 = white
         */
        private static ConsoleColor ColorToConsole(short num, ConsoleColor current, bool background)
        {
            return num switch
            {
                0 => current,
                1 => background ? ConsoleColor.Black : ConsoleColor.Gray,
                2 => ConsoleColor.Black,
                3 => ConsoleColor.DarkRed,
                4 => ConsoleColor.DarkGreen,
                5 => ConsoleColor.DarkYellow,
                6 => ConsoleColor.DarkBlue,
                7 => ConsoleColor.DarkMagenta,
                8 => ConsoleColor.DarkCyan,
                9 => ConsoleColor.Gray,
                _ => current,
            };
        }

        private static ConsoleColor EmphasizeColor(ConsoleColor color)
        {
            return color switch
            {
                ConsoleColor.Black => ConsoleColor.DarkGray,
                ConsoleColor.DarkRed => ConsoleColor.Red,
                ConsoleColor.DarkGreen => ConsoleColor.Green,
                ConsoleColor.DarkYellow => ConsoleColor.Yellow,
                ConsoleColor.DarkBlue => ConsoleColor.Blue,
                ConsoleColor.DarkMagenta => ConsoleColor.Magenta,
                ConsoleColor.DarkCyan => ConsoleColor.Cyan,
                ConsoleColor.Gray => ConsoleColor.White,
                _ => color,
            };
        }

        public byte WidthChars => (byte)Console.WindowWidth;

        public short WidthUnits => (short)Console.WindowWidth;

        public byte HeightChars => (byte)Console.WindowHeight;

        public short HeightUnits => (short)Console.WindowHeight;

        public byte FontHeight => 1;

        public byte FontWidth => 1;

        public event EventHandler? SizeChanged;

        public bool ColorsAvailable => true;

        public byte DefaultForeground => 9; // white

        public byte DefaultBackground => 2; // black

        public Stream? OpenSaveFile(int size)
        {
            var defaultFile = fileBase + ".sav";

            FlushBuffer();
            lineCount = 0;

            string? filename;
            do
            {
                Console.Write("Enter a new saved game file (\".\" to quit) [{0}]: ",
                    defaultFile);
                filename = Console.ReadLine();
                if (filename == "")
                    filename = defaultFile;

                if (filename == ".")
                    return null;

                if (File.Exists(filename))
                {
                    if (!YesOrNoPrompt($"\"{filename}\" exists. Are you sure (y/n)? "))
                        filename = null;
                }
            }
            while (filename == null);

            return new FileStream(filename, FileMode.Create, FileAccess.Write);
        }

        public Stream? OpenRestoreFile()
        {
            FlushBuffer();
            lineCount = 0;

            string filename;
            do
            {
                Console.Write("Enter an existing saved game file (blank to cancel): ");
                filename = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(filename))
                    return null;

                if (File.Exists(filename))
                    break;
            }
            while (true);

            return new FileStream(filename, FileMode.Open, FileAccess.Read);
        }

        public Stream? OpenAuxiliaryFile(string name, int size, bool writing)
        {
            if (InvalidAuxFileName(name))
                return null;

            try
            {
                return new FileStream(name,
                    writing ? FileMode.Create : FileMode.Open,
                    writing ? FileAccess.Write : FileAccess.Read);
            }
            catch
            {
                return null;
            }
        }

        public Stream? OpenCommandFile(bool writing)
        {
            FlushBuffer();
            lineCount = 0;

            string filename;
            if (SuppliedCommandFile != null)
            {
                filename = SuppliedCommandFile;
                SuppliedCommandFile = null;
            }
            else
            {
                do
                {
                    Console.Write("Enter the name of a command file to {0} (blank to cancel): ",
                        writing ? "record" : "play back");
                    filename = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(filename))
                        return null;

                    if (writing)
                    {
                        // if the file exists, prompt to overwrite it
                        if (!File.Exists(filename) || YesOrNoPrompt($"\"{filename}\" exists. Are you sure (y/n)? "))
                            break;
                    }
                    else
                    {
                        // the file must already exist
                        if (File.Exists(filename))
                            break;
                    }
                }
                while (true);
            }

            return new FileStream(filename,
                writing ? FileMode.Create : FileMode.Open,
                writing ? FileAccess.Write : FileAccess.Read);
        }

        private static bool YesOrNoPrompt(string prompt)
        {
            string yorn;
            do
            {
                Console.Write(prompt);
                yorn = Console.ReadLine()?.ToLower().Trim() ?? "n";
            } while (yorn.Length == 0);

            return yorn[0] == 'y';
        }

        private static readonly byte[] DummyTerminatingKeys = [];

        private async Task<bool> YesOrNoPromptAsync(string prompt, CancellationToken cancellationToken = default)
        {
            string yorn = "";
            do
            {
                Console.Write(prompt);
                cancellationToken.ThrowIfCancellationRequested();

                var rlr = await ReadLineAsync("", DummyTerminatingKeys, false, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (rlr.Outcome != ReadOutcome.KeyPressed)
                    continue;

                yorn = rlr.Text.ToLower().Trim();
            } while (yorn.Length == 0);

            return yorn[0] == 'y';
        }

        private static readonly System.Buffers.SearchValues<char> s_badChars = System.Buffers.SearchValues.Create(":\"<>\\/*?|");

        private static bool InvalidAuxFileName([NotNull] string name)
        {
            if (name.Trim().Length == 0)
                return true;

            return name.AsSpan().IndexOfAny(s_badChars) > 0;
        }

        public short SetFont(short num)
        {
            // basic support for the normal font
            if (num == 1)
                return 1;

            // no font changes supported
            return 0;
        }

        public bool GraphicsFontAvailable => false;

        public void PlaySoundSample(ushort num, SoundAction action, byte volume, byte repeats,
            SoundFinishedCallback callback)
        {
            // not supported
        }

        public void PlayBeep(bool highPitch)
        {
            Console.Beep(highPitch ? 1600 : 800, 200);
        }

        public bool SoundSamplesAvailable => false;

        public bool Buffering
        {
            get => buffering;

            set
            {
                if (buffering != value)
                {
                    if (buffering)
                        FlushBuffer();
                    buffering = value;
                }
            }
        }

        public UnicodeCaps CheckUnicode(char ch)
        {
            // naive
            return UnicodeCaps.CanInput | UnicodeCaps.CanPrint;
        }

        public bool DrawCustomStatusLine(string location, short hoursOrScore, short minsOrTurns, bool useTime)
        {
            return false;
        }

        private void FlushBuffer()
        {
            // first, take the opportunity to check for console resize
            if (Console.WindowWidth != prevWinWidth || Console.WindowHeight != prevWinHeight)
            {
                prevWinWidth = Console.WindowWidth;
                prevWinHeight = Console.WindowHeight;
                SizeChanged?.Invoke(this, EventArgs.Empty);
            }

            // then flush the buffer
            foreach (var item in buffer)
            {
                if ((item & STYLE_FLAG) == 0)
                {
                    CheckScroll(item == '\n');
                    Console.Write((char)item);
                    CheckMore();
                }
                else
                {
                    Console.ForegroundColor = (ConsoleColor)(item & 0xFFFF);
                    Console.BackgroundColor = (ConsoleColor)((item >> 16) & 0x7FFF);
                }
            }

            buffer.RemoveRange(0, buffer.Count);
            bufferLength = 0;
        }

        private void CheckScroll(bool force = false)
        {
            if (weakConsole)
                return;

            if (split > 0)
            {
                var atRightEdge = Console.CursorLeft == Console.BufferWidth - 1;
                var onLastLine = Console.CursorTop == Console.BufferHeight - 1;

                if (onLastLine && (atRightEdge || force))
                {
                    Console.MoveBufferArea(0, 0, Console.BufferWidth, split, 0, 1);
                }
            }
        }

        private void CheckMore()
        {
            if (!HideMorePrompts && !upper && Console.CursorLeft == 0)
            {
                lineCount++;
                if (lineCount >= Console.WindowHeight - split - 1)
                {
                    Console.Write("-- more --");
                    Console.ReadKey(true);

                    // erase the prompt
                    Console.Write("\b\b\b\b\b\b\b\b\b\b");
                    Console.Write("          ");
                    Console.Write("\b\b\b\b\b\b\b\b\b\b");

                    lineCount = 0;
                }
            }
        }

        private async Task CheckMoreAsync()
        {
            if (!HideMorePrompts && !upper && Console.CursorLeft == 0)
            {
                lineCount++;
                if (lineCount >= Console.WindowHeight - split - 1)
                {
                    Console.Write("-- more --");
                    await DoConsoleAsync(() => Console.ReadKey(true)).ConfigureAwait(false);

                    // erase the prompt
                    Console.Write("\b\b\b\b\b\b\b\b\b\b");
                    Console.Write("          ");
                    Console.Write("\b\b\b\b\b\b\b\b\b\b");

                    lineCount = 0;
                }
            }
        }

        [NotNull]
        private static Task<T> DoConsoleAsync<T>([NotNull] Func<T> consoleOperation)
        {
            return Task.Factory.StartNew(consoleOperation, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private async Task FlushBufferAsync()
        {
            // first, take the opportunity to check for console resize
            if (Console.WindowWidth != prevWinWidth || Console.WindowHeight != prevWinHeight)
            {
                prevWinWidth = Console.WindowWidth;
                prevWinHeight = Console.WindowHeight;
                SizeChanged?.Invoke(this, EventArgs.Empty);
            }

            // then flush the buffer
            foreach (var item in buffer)
            {
                if ((item & STYLE_FLAG) == 0)
                {
                    CheckScroll(item == '\n');
                    Console.Write((char)item);
                    await CheckMoreAsync().ConfigureAwait(false);
                }
                else
                {
                    Console.ForegroundColor = (ConsoleColor)(item & 0xFFFF);
                    Console.BackgroundColor = (ConsoleColor)((item >> 16) & 0x7FFF);
                }
            }

            buffer.RemoveRange(0, buffer.Count);
            bufferLength = 0;
        }

        const int POLL_INTERVAL_MS = 100;

        public async Task<ReadLineResult> ReadLineAsync(string initial, byte[] terminatingKeys, bool allowDebuggerBreak,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await FlushBufferAsync().ConfigureAwait(false);
            lineCount = 0;

            var histIdx = history.Count;
            var savedEntry = string.Empty;
            byte terminator;

            StringBuilder sb;
            int cursor;
            if (initial.Length == 0)
            {
                sb = new StringBuilder(20);
                cursor = 0;
            }
            else
            {
                sb = new StringBuilder(initial);
                cursor = initial.Length;
            }

            void ClearInput()
            {
                for (var i = cursor; i < sb.Length; i++)
                    Console.Write(' ');
                for (var i = 0; i < sb.Length; i++)
                    Console.Write("\x08 \x08");
                sb.Length = 0;
                cursor = 0;
            }

            while (true)
            {
                while (!Console.KeyAvailable && !cancellationToken.IsCancellationRequested)
                    await Task.Delay(POLL_INTERVAL_MS, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                var info = Console.ReadKey(true);
                var special = ConsoleKeyToZSCII(info.Key);
                if (IsTerminator(special, terminatingKeys))
                {
                    terminator = special;
                    break;
                }

                switch (info.Key)
                {
                    case ConsoleKey.LeftArrow:
                        if (cursor > 0)
                        {
                            cursor--;
                            Console.Write('\x08');
                        }

                        break;

                    case ConsoleKey.RightArrow:
                        if (cursor < sb.Length)
                        {
                            Console.Write(sb[cursor]);
                            cursor++;
                        }

                        break;

                    case ConsoleKey.Home:
                        while (cursor > 0)
                        {
                            cursor--;
                            Console.Write('\x08');
                        }

                        break;

                    case ConsoleKey.End:
                        while (cursor < sb.Length)
                        {
                            Console.Write(sb[cursor]);
                            cursor++;
                        }

                        break;

                    case ConsoleKey.UpArrow:
                        if (histIdx > 0 && history.Count > 0)
                        {
                            if (histIdx == history.Count)
                                savedEntry = sb.ToString();

                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(' ');
                            for (var i = 0; i < sb.Length; i++)
                                Console.Write("\x08 \x08");

                            histIdx--;
                            sb.Length = 0;
                            sb.Append(history[histIdx]);
                            Console.Write(sb.ToString());
                            cursor = sb.Length;
                        }

                        break;

                    case ConsoleKey.DownArrow:
                        if (histIdx < history.Count && history.Count > 0)
                        {
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(' ');
                            for (var i = 0; i < sb.Length; i++)
                                Console.Write("\x08 \x08");

                            histIdx++;
                            sb.Length = 0;
                            sb.Append(histIdx == history.Count ? savedEntry : history[histIdx]);
                            Console.Write(sb.ToString());
                            cursor = sb.Length;
                        }

                        break;

                    case ConsoleKey.Backspace:
                        if (cursor > 0)
                        {
                            cursor--;
                            sb.Remove(cursor, 1);
                            Console.Write('\x08');
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(sb[i]);
                            Console.Write(' ');
                            for (var i = cursor; i <= sb.Length; i++)
                                Console.Write('\x08');
                        }

                        break;

                    case ConsoleKey.Delete:
                        if (cursor < sb.Length)
                        {
                            sb.Remove(cursor, 1);
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(sb[i]);
                            Console.Write(' ');
                            for (var i = cursor; i <= sb.Length; i++)
                                Console.Write('\x08');
                        }

                        break;

                    case ConsoleKey.Escape:
                        ClearInput();
                        break;

                    case ConsoleKey.B:
                        if (allowDebuggerBreak && info.Modifiers == ConsoleModifiers.Alt)
                        {
                            // debugger break
                            CheckScroll(true);
                            ClearInput();
                            return ReadLineResult.DebuggerBreak;
                        }
                        else
                        {
                            goto default;
                        }

                    default:
                        if (info.KeyChar != '\0')
                        {
                            sb.Insert(cursor, info.KeyChar);
                            Console.Write(info.KeyChar);
                            cursor++;
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write(sb[i]);
                            for (var i = cursor; i < sb.Length; i++)
                                Console.Write('\x08');
                        }

                        break;
                }
            }

            if (terminator == 13)
            {
                CheckScroll(true);
                Console.WriteLine();
            }

            var result = sb.ToString() ?? throw new InvalidOperationException();

            history.Add(result);
            if (history.Count > MAX_COMMAND_HISTORY)
                history.RemoveAt(0);

            return ReadLineResult.LineEntered(result, terminator);
        }

        public async Task<short> ReadKeyAsync(CharTranslator translator, CancellationToken cancellationToken = default)
        {
            await FlushBufferAsync().ConfigureAwait(false);
            lineCount = 0;

            while (true)
            {
                while (!Console.KeyAvailable && !cancellationToken.IsCancellationRequested)
                    await Task.Delay(POLL_INTERVAL_MS, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                var info = Console.ReadKey(true);
                short zkey = ConsoleKeyToZSCII(info.Key);
                if (zkey != 0)
                    return zkey;

                zkey = translator(info.KeyChar);
                if (zkey != 0)
                    return zkey;
            }
        }

        public async Task<Stream?> OpenSaveFileAsync(int size, CancellationToken cancellationToken = default)
        {
            var defaultFile = fileBase + ".sav";

            await FlushBufferAsync();
            lineCount = 0;

            string? filename;
            do
            {
                Console.Write("Enter a new saved game file (\".\" to quit) [{0}]: ",
                    defaultFile);
                filename = Console.ReadLine();
                if (filename == "")
                    filename = defaultFile;

                if (filename == ".")
                    return null;

                if (File.Exists(filename))
                {
                    if (!await YesOrNoPromptAsync($"\"{filename}\" exists. Are you sure (y/n)? ", cancellationToken))
                        filename = null;
                }
            }
            while (filename == null);

            return new FileStream(filename, FileMode.Create, FileAccess.Write);
        }

        public async Task<Stream?> OpenRestoreFileAsync(CancellationToken cancellationToken = default)
        {
            await FlushBufferAsync().ConfigureAwait(false);
            lineCount = 0;

            string filename;
            do
            {
                Console.Write("Enter an existing saved game file (blank to cancel): ");
                filename = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(filename))
                    return null;
            }
            while (!File.Exists(filename));

            return new FileStream(filename, FileMode.Open, FileAccess.Read);
        }

        public Task<Stream?> OpenAuxiliaryFileAsync(string name, int size, bool writing, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(OpenAuxiliaryFile(name, size, writing));
        }

        public async Task<Stream?> OpenCommandFileAsync(bool writing, CancellationToken cancellationToken = default)
        {
            await FlushBufferAsync().ConfigureAwait(false);
            lineCount = 0;

            string filename;
            if (SuppliedCommandFile != null)
            {
                filename = SuppliedCommandFile;
                SuppliedCommandFile = null;
            }
            else
            {
                do
                {
                    Console.Write("Enter the name of a command file to {0} (blank to cancel): ",
                        writing ? "record" : "play back");
                    filename = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(filename))
                        return null;

                    if (writing)
                    {
                        // if the file exists, prompt to overwrite it
                        if (!File.Exists(filename) ||
                            await YesOrNoPromptAsync($"\"{filename}\" exists. Are you sure (y/n)? ", cancellationToken)
                                .ConfigureAwait(false))
                            break;
                    }
                    else
                    {
                        // the file must already exist
                        if (File.Exists(filename))
                            break;
                    }
                }
                while (true);
            }

            return new FileStream(filename,
                writing ? FileMode.Create : FileMode.Open,
                writing ? FileAccess.Write : FileAccess.Read);
        }
    }
}