using System;
using System.Text;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Central policy for the progress watchdog that replaced the fixed wall-clock timeouts.
    /// A run is auto-terminated only when it is demonstrably stuck (no progress) or looping,
    /// never merely because it is slow: slow-but-steady generations keep emitting events and
    /// are left alone.
    /// </summary>
    public static class WatchdogPolicy
    {
        /// <summary>Inactivity that counts as "stuck": the run produced no progress event for this long.</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

        /// <summary>Consecutive identical (tool + arguments) calls that count as a dead loop.</summary>
        public const int ToolRepeatThreshold = 3;

        /// <summary>Minimum repeat unit length (characters) the repeated-text detector recognizes.</summary>
        public const int ModelRepeatMinUnit = 8;

        /// <summary>Largest window of recent model text kept for repeated-text detection.</summary>
        public const int ModelRepeatWindow = 256;
    }

    /// <summary>
    /// Detects an LLM looping on its own output. It is fed recent model-text fragments and
    /// reports true when the tail is the same unit repeated four times in a row. Conservative on
    /// purpose so legitimate output never trips it: the unit must be at least
    /// <see cref="WatchdogPolicy.ModelRepeatMinUnit"/> characters and must contain varied
    /// non-whitespace characters (single-character or whitespace repeats are ignored).
    /// </summary>
    public sealed class TextLoopDetector
    {
        private readonly object _gate = new object();
        private readonly StringBuilder _buf = new StringBuilder();

        /// <summary>Feed a fragment of model output. Returns true when the recent output loops on itself.</summary>
        public bool Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            lock (_gate)
            {
                _buf.Append(text);
                int cap = WatchdogPolicy.ModelRepeatWindow;
                if (_buf.Length > cap)
                    _buf.Remove(0, _buf.Length - cap);

                int n = _buf.Length;
                int minUnit = WatchdogPolicy.ModelRepeatMinUnit;
                if (n < minUnit * 4) return false;

                // The tail loops if the last 4*unit characters are one `unit` repeated four times.
                int maxUnit = Math.Min(32, n / 4);
                for (int unit = minUnit; unit <= maxUnit; unit++)
                {
                    if (IsRepeat(unit)) return true;
                }
            }

            return false;
        }

        private bool IsRepeat(int unit)
        {
            int n = _buf.Length;
            int start = n - unit * 4;
            bool varied = false;
            char first = '\0';
            for (int i = 0; i < unit; i++)
            {
                char c = _buf[start + unit * 3 + i];
                for (int r = 0; r < 4; r++)
                {
                    if (_buf[start + unit * r + i] != c) return false;
                }

                if (!char.IsWhiteSpace(c))
                {
                    if (first == '\0') first = c;
                    else if (c != first) varied = true;
                }
            }

            return varied;
        }
    }
}