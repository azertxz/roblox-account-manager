using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;

namespace RBX_Alt_Manager.Classes
{
    internal static class AntiAfkService
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int FocusSwitchDelayMs = 120;
        private const int KeyPressDelayMs = 40;

        private static readonly int[] KeyPool =
        {
            0x57, // W
            0x41, // A
            0x53, // S
            0x44, // D
            0x20, // Space
            0x25, // Left arrow
            0x27  // Right arrow
        };

        private static readonly object ConfigLock = new object();
        private static readonly Random Rng = new Random();

        private static System.Timers.Timer TickTimer;
        private static bool Enabled;
        private static bool UseBackgroundInput;
        private static int MinSeconds = 75;
        private static int MaxSeconds = 240;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        private struct AntiAfkCycleResult
        {
            public int Total;
            public int Sent;
            public int SkippedNoWindow;
            public int SkippedInvisible;
            public int Errors;
        }

        public static void Configure(bool enabled, int minSeconds, int maxSeconds, bool useBackgroundInput)
        {
            lock (ConfigLock)
            {
                Enabled = enabled;
                UseBackgroundInput = useBackgroundInput;
                MinSeconds = Math.Max(5, minSeconds);
                MaxSeconds = Math.Max(MinSeconds, maxSeconds);

                if (!Enabled)
                {
                    TickTimer?.Stop();
                    Program.Logger.Info("Anti-AFK disabled");
                    return;
                }

                if (TickTimer == null)
                {
                    TickTimer = new System.Timers.Timer();
                    TickTimer.AutoReset = false;
                    TickTimer.Elapsed += OnTick;
                }

                ScheduleNext();
                Program.Logger.Info($"Anti-AFK enabled ({(UseBackgroundInput ? "background" : "visible")}) with interval {MinSeconds}-{MaxSeconds}s");
            }
        }

        public static void Stop()
        {
            lock (ConfigLock)
            {
                Enabled = false;
                TickTimer?.Stop();
            }
        }

        private static void OnTick(object sender, ElapsedEventArgs e)
        {
            try
            {
                if (!Enabled || Program.Closed)
                    return;

                AntiAfkCycleResult result = SendInputs();
                Program.Logger.Info($"Anti-AFK cycle completed ({(UseBackgroundInput ? "background" : "visible")}): sent={result.Sent}/{result.Total}, skipped_no_window={result.SkippedNoWindow}, skipped_invisible={result.SkippedInvisible}, errors={result.Errors}");
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"Anti-AFK tick error: {ex}");
            }
            finally
            {
                lock (ConfigLock)
                {
                    if (Enabled && !Program.Closed)
                        ScheduleNext();
                }
            }
        }

        private static void ScheduleNext()
        {
            if (TickTimer == null)
                return;

            TickTimer.Stop();

            int intervalSeconds = Rng.Next(MinSeconds, MaxSeconds + 1);
            TickTimer.Interval = intervalSeconds * 1000;
            TickTimer.Start();
        }

        private static AntiAfkCycleResult SendInputs()
        {
            AntiAfkCycleResult result = new AntiAfkCycleResult();
            IntPtr previouslyFocusedWindow = GetForegroundWindow();
            Process[] processes = Process.GetProcessesByName("RobloxPlayerBeta").OrderBy(proc => proc.Id).ToArray();

            result.Total = processes.Length;

            foreach (Process process in processes)
            {
                try
                {
                    if (process.HasExited)
                    {
                        result.SkippedNoWindow++;
                        continue;
                    }

                    IntPtr windowHandle = process.MainWindowHandle;

                    if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
                    {
                        result.SkippedNoWindow++;
                        continue;
                    }

                    if (!IsWindowVisible(windowHandle))
                    {
                        result.SkippedInvisible++;
                        continue;
                    }

                    int key = KeyPool[Rng.Next(KeyPool.Length)];

                    if (UseBackgroundInput)
                    {
                        if (TrySendBackgroundKey(windowHandle, key))
                            result.Sent++;
                        else
                            result.Errors++;

                        continue;
                    }

                    if (!ActivateWindow(windowHandle))
                    {
                        result.Errors++;
                        continue;
                    }

                    Thread.Sleep(FocusSwitchDelayMs);

                    if (TrySendVisibleKey(windowHandle, key))
                        result.Sent++;
                    else
                        result.Errors++;

                    Thread.Sleep(KeyPressDelayMs);
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    Program.Logger.Error($"Anti-AFK process input error ({process.Id}): {ex.Message}");
                }
            }

            if (!UseBackgroundInput && previouslyFocusedWindow != IntPtr.Zero)
                ActivateWindow(previouslyFocusedWindow);

            return result;
        }

        private static bool ActivateWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
                return false;

            if (IsIconic(windowHandle))
                ShowWindowAsync(windowHandle, SW_RESTORE);

            ShowWindowAsync(windowHandle, SW_SHOW);

            bool focused = SetForegroundWindow(windowHandle);

            if (!focused)
            {
                try
                {
                    SwitchToThisWindow(windowHandle, true);
                }
                catch
                {
                }

                Thread.Sleep(60);
                focused = GetForegroundWindow() == windowHandle;
            }

            return focused || GetForegroundWindow() == windowHandle;
        }

        private static bool TrySendBackgroundKey(IntPtr windowHandle, int key)
        {
            bool down = Utilities.PostMessage(windowHandle, WM_KEYDOWN, key, 0);
            bool up = Utilities.PostMessage(windowHandle, WM_KEYUP, key, 0);
            return down && up;
        }

        private static bool TrySendVisibleKey(IntPtr windowHandle, int key)
        {
            if (TrySendForegroundKey((ushort)key))
                return true;

            return TrySendBackgroundKey(windowHandle, key);
        }

        private static bool TrySendForegroundKey(ushort key)
        {
            INPUT[] inputs =
            {
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = key,
                            wScan = 0,
                            dwFlags = 0,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                },
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = key,
                            wScan = 0,
                            dwFlags = KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                }
            };

            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            return sent == (uint)inputs.Length;
        }
    }
}
