/**************************************************************************************************
 * This will wrap cmd.exe, while allowing you to use the mouse to edit the history.
 * 
 * Clicking somewhere in the history will put you in Edit mode, allowing you to type over the old text.
 * 
 * While in Edit mode, you can press Enter to go back to Command mode. Going back to Command mode
 * will move the cursor back to the end of the output. You can also click in any location past
 * the end of the output to switch the mode from Edit back to Command.
 * 
 * Current Limitations:
 *    - The arrow keys don't work.
 *    - Occasionally a phantom cursor will appear in the history. I think this is a threading issue.
 **************************************************************************************************/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static CmdWrapper.NativeMethods;

namespace CmdWrapper
{
    class Program
    {
        private const string REAL_CMD_PATH = @"C:\Windows\System32\cmd.exe";

        private static Process _cmdProc;
        private static int _conHostProcId;
        private static ConsolePipe _consolePipe;
        private static object _sync;
        
        static void Main(string[] args)
        {
            IntPtr inHandle = GetStdHandle(STD_INPUT_HANDLE);
            uint mode = 0;

            GetConsoleMode(inHandle, ref mode);

            Console.CancelKeyPress += CancelKeyPressHandler;

            //mode &= ~ENABLE_PROCESSED_INPUT; //disable
            mode &= ~ENABLE_LINE_INPUT; //disable
            mode &= ~ENABLE_QUICK_EDIT_MODE; //disable         
            //mode |= ENABLE_WINDOW_INPUT; //enable (if you want)
            mode |= ENABLE_MOUSE_INPUT; //enable

            SetConsoleMode(inHandle, mode);

            _cmdProc = Process.Start(new ProcessStartInfo
            {
                FileName = REAL_CMD_PATH,
                Arguments = "/Q",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            });

            MouseEvent += MouseEventHandler;

            var conOutput = Console.OpenStandardOutput();
            _sync = new object();

            _consolePipe = new ConsolePipe(conOutput, _cmdProc.StandardInput.BaseStream, _sync);

            StreamPipe.Open(_cmdProc.StandardOutput.BaseStream, conOutput, _sync);
            StreamPipe.Open(_cmdProc.StandardError.BaseStream, Console.OpenStandardError(), _sync);

            StartConsoleListener();

            _conHostProcId = GetConHostProcId();

            _cmdProc.WaitForExit();
        }

        private static int GetConHostProcId()
        {
            while(true)
            {
                foreach (var childProc in EnumerateChildProcesses(_cmdProc))
                {
                    if (childProc.ProcessName == "conhost")
                        return childProc.Id;
                }
            }
        }

        private static void CancelKeyPressHandler(object sender, ConsoleCancelEventArgs e)
        {
            foreach (var childProc in EnumerateChildProcesses(_cmdProc))
            {
                if (childProc.Id != _conHostProcId)
                    childProc.Kill();
            }

            e.Cancel = true;
        }

        private static void MouseEventHandler(MOUSE_EVENT_RECORD r)
        {
            if ((r.dwButtonState & MOUSE_EVENT_RECORD.FROM_LEFT_1ST_BUTTON_PRESSED) == MOUSE_EVENT_RECORD.FROM_LEFT_1ST_BUTTON_PRESSED)
            {
                lock (_sync)
                {
                    _consolePipe.EnableEditMode();

                    var mouseY = r.dwMousePosition.Y;

                    if (mouseY < _consolePipe.CommandModeTop)
                    {
                        Console.SetCursorPosition(r.dwMousePosition.X, mouseY);
                        return;
                    }
                    else if (mouseY == _consolePipe.CommandModeTop)
                    {
                        var mouseX = r.dwMousePosition.X;

                        if (mouseX < _consolePipe.CommandModeLeft)
                        {
                            Console.SetCursorPosition(mouseX, mouseY);
                            return;
                        }
                    }

                    _consolePipe.EnableCommandMode();
                }
            }
        }

        private static event ConsoleMouseEvent MouseEvent;

        private static void StartConsoleListener()
        {
            var thread = new Thread(ConsoleListener);

            thread.IsBackground = true;
            thread.Start();
        }

        private static void ConsoleListener()
        {
            var handleIn = GetStdHandle(STD_INPUT_HANDLE);
            var buffer = new INPUT_RECORD[1];
            var record = buffer[0];

            while (true)
            {
                uint numRead = 0;

                ReadConsoleInput(handleIn, buffer, 1, ref numRead);

                if (numRead == 1)
                {
                    switch (buffer[0].EventType)
                    {
                        case INPUT_RECORD.MOUSE_EVENT:
                            MouseEvent?.Invoke(buffer[0].MouseEvent);
                            break;

                        case INPUT_RECORD.KEY_EVENT:
                            WriteConsoleInput(handleIn, buffer, 1, ref numRead);
                            _consolePipe.Run();
                            break;
                    }
                }
            }
        }

        private delegate void ConsoleMouseEvent(MOUSE_EVENT_RECORD r);

        private static IEnumerable<Process> EnumerateChildProcesses(Process process)
        {
            var scope = new ManagementScope();
            scope.Connect();

            if (scope.IsConnected)
            {
                var query = new ObjectQuery($"SELECT ProcessId FROM win32_process WHERE ParentProcessId={process.Id}");

                using (var searcher = new ManagementObjectSearcher(scope, query))
                using (var result = searcher.Get())
                {
                    Process proc;

                    foreach (var item in result)
                    {
                        var procId = (uint)item["ProcessId"];

                        try
                        {
                            proc = Process.GetProcessById((int)procId);
                        }
                        catch (ArgumentException)
                        {
                            continue;
                        }

                        yield return proc;
                    }                    
                }
            }
        }
    }


    public static class NativeMethods
    {
        public struct COORD
        {
            public short X;
            public short Y;

            public COORD(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUT_RECORD
        {
            public const ushort KEY_EVENT = 0x0001;
            public const ushort MOUSE_EVENT = 0x0002;

            [FieldOffset(0)]
            public ushort EventType;

            [FieldOffset(4)]
            public KEY_EVENT_RECORD KeyEvent;

            [FieldOffset(4)]
            public MOUSE_EVENT_RECORD MouseEvent;

            [FieldOffset(4)]
            public WINDOW_BUFFER_SIZE_RECORD WindowBufferSizeEvent;
        }

        public struct MOUSE_EVENT_RECORD
        {
            public COORD dwMousePosition;

            public const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001,
                FROM_LEFT_2ND_BUTTON_PRESSED = 0x0004,
                FROM_LEFT_3RD_BUTTON_PRESSED = 0x0008,
                FROM_LEFT_4TH_BUTTON_PRESSED = 0x0010,
                RIGHTMOST_BUTTON_PRESSED = 0x0002;
            public uint dwButtonState;

            public const int CAPSLOCK_ON = 0x0080,
                ENHANCED_KEY = 0x0100,
                LEFT_ALT_PRESSED = 0x0002,
                LEFT_CTRL_PRESSED = 0x0008,
                NUMLOCK_ON = 0x0020,
                RIGHT_ALT_PRESSED = 0x0001,
                RIGHT_CTRL_PRESSED = 0x0004,
                SCROLLLOCK_ON = 0x0040,
                SHIFT_PRESSED = 0x0010;
            public uint dwControlKeyState;

            public const int DOUBLE_CLICK = 0x0002,
                MOUSE_HWHEELED = 0x0008,
                MOUSE_MOVED = 0x0001,
                MOUSE_WHEELED = 0x0004;
            public uint dwEventFlags;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        public struct KEY_EVENT_RECORD
        {
            [FieldOffset(0)]
            public bool bKeyDown;
            [FieldOffset(4)]
            public ushort wRepeatCount;
            [FieldOffset(6)]
            public ushort wVirtualKeyCode;
            [FieldOffset(8)]
            public ushort wVirtualScanCode;
            [FieldOffset(10)]
            public char UnicodeChar;
            [FieldOffset(10)]
            public byte AsciiChar;

            public const int CAPSLOCK_ON = 0x0080,
                ENHANCED_KEY = 0x0100,
                LEFT_ALT_PRESSED = 0x0002,
                LEFT_CTRL_PRESSED = 0x0008,
                NUMLOCK_ON = 0x0020,
                RIGHT_ALT_PRESSED = 0x0001,
                RIGHT_CTRL_PRESSED = 0x0004,
                SCROLLLOCK_ON = 0x0040,
                SHIFT_PRESSED = 0x0010;

            [FieldOffset(12)]
            public uint dwControlKeyState;
        }

        public struct WINDOW_BUFFER_SIZE_RECORD
        {
            public COORD dwSize;
        }

        public const uint STD_INPUT_HANDLE = unchecked((uint)-10);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(uint nStdHandle);

        public const uint ENABLE_MOUSE_INPUT = 0x0010,
            ENABLE_QUICK_EDIT_MODE = 0x0040,
            ENABLE_EXTENDED_FLAGS = 0x0080,
            ENABLE_PROCESSED_INPUT = 0x0001,
            ENABLE_LINE_INPUT = 0x0002,
            ENABLE_ECHO_INPUT = 0x0004,
            ENABLE_WINDOW_INPUT = 0x0008; //more

        [DllImport("kernel32.dll")]
        public static extern bool GetConsoleMode(IntPtr hConsoleInput, ref uint lpMode);

        [DllImport("kernel32.dll")]
        public static extern bool SetConsoleMode(IntPtr hConsoleInput, uint dwMode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool ReadConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, ref uint lpNumberOfEventsRead);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength, ref uint lpNumberOfEventsWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);    

        public const uint CTRL_C_EVENT = 0;
    }

    public class StreamPipe
    {
        public static StreamPipe Open(Stream input, Stream output, object writeSync)
        {
            var pipe = new StreamPipe(input, output, writeSync);
            pipe.Open();

            return pipe;
        }

        private const int BUFFER_SIZE = 4096;

        private byte[] _buffer;

        private Stream _input;
        private Stream _output;
        private object _sync;

        private StreamPipe(Stream input, Stream output, object sync)
        {
            _input = input;
            _output = output;
            _sync = sync;

            _buffer = new byte[BUFFER_SIZE];
        }

        private async void Open()
        {
            while (true)
            {
                var bytesRead = await _input.ReadAsync(_buffer, 0, BUFFER_SIZE);

                if (bytesRead <= 0)
                    return;

                lock (_sync)
                {
                    _output.Write(_buffer, 0, bytesRead);
                    _output.Flush();
                }
            }
        }
    }

    public class ConsolePipe
    {
        private StreamWriter _consoleWriter;
        private StreamWriter _cmdWriter;
        private StringBuilder _cmdBuffer;
        private object _sync;

        public ConsolePipe(Stream consoleStream, Stream cmdStream, object writeSync)
        {
            _consoleWriter = new StreamWriter(consoleStream) { AutoFlush = true };
            _cmdWriter = new StreamWriter(cmdStream) { AutoFlush = true };
            _cmdBuffer = new StringBuilder(64);
            _sync = writeSync;
        }

        public void Run()
        {
            lock (_sync)
            {
                while (Console.KeyAvailable)
                {
                    var ch = (char)Console.Read();

                    switch (ch)
                    {
                        case '\r': //Return

                            if (Mode == ConsoleMode.Command)
                            {
                                _consoleWriter.WriteLine();

                                ProcessCommand(_cmdBuffer.ToString());

                                _cmdBuffer.Clear();
                            }
                            else
                            {
                                EnableCommandMode();
                            }

                            break;

                        case '\b': //Backspace

                            switch (Mode)
                            {
                                case ConsoleMode.Command:

                                    if (_cmdBuffer.Length > 0)
                                    {
                                        _consoleWriter.Write("\b \b");
                                        
                                        _cmdBuffer.Remove(_cmdBuffer.Length - 1, 1);
                                    }

                                    break;

                                case ConsoleMode.Edit:

                                    _consoleWriter.Write("\b \b");
                                    
                                    break;
                            }

                            break;

                        default:

                            _consoleWriter.Write(ch);
                            
                            if (Mode == ConsoleMode.Command)
                                _cmdBuffer.Append(ch);

                            break;
                    }
                }
            }
        }

        public ConsoleMode Mode;

        public int CommandModeLeft;
        public int CommandModeTop;

        public void EnableEditMode()
        {
            if (Mode != ConsoleMode.Edit)
            {
                CommandModeLeft = Console.CursorLeft;
                CommandModeTop = Console.CursorTop;

                Mode = ConsoleMode.Edit;
            }
        }

        public void EnableCommandMode()
        {
            if (Mode != ConsoleMode.Command)
            {
                Console.SetCursorPosition(CommandModeLeft, CommandModeTop);
                Mode = ConsoleMode.Command;
            }
        }

        private void ProcessCommand(string command)
        {
            if (String.Compare(command, "cls", true) == 0)
            {
                Console.Clear();
                _cmdWriter.WriteLine();
            }
            else
            {
                _cmdWriter.WriteLine(command);
            }
        }
    }

    public enum ConsoleMode
    {
        Command,
        Edit
    }
}