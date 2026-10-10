// Server Meshing test rig — database burst, pure part (Phase 0 step 9 c1): the MESH_DB_BURST grammar, the burst
// characters' names, the arrival schedule, the nearest-rank percentile and the DB burst lines (step9_design.md "Log
// lines"). Nothing here touches the kit's server code, so it compiles and is tested in every build.
using System;
using System.Globalization;
using System.Text;
using MultiplayerARPG.MMO.Mesh.Sync;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshDbBurst
    {
        /// <summary>EnvironmentKey is the burst's setting: <c>&lt;N&gt;@&lt;t&gt;</c> or <c>&lt;R&gt;/s@&lt;t&gt;+&lt;d&gt;</c>.</summary>
        public const string EnvironmentKey = "MESH_DB_BURST";
        /// <summary>BurstObjectName names the burst's GameObject.</summary>
        public const string BurstObjectName = "[Mesh] DbBurst";
        /// <summary>MaxArrivals is the most arrivals one burst brings (the burst rows are numbered with four digits).</summary>
        public const int MaxArrivals = 9999;
        /// <summary>MaxStartSeconds is the latest start a setting may ask for.</summary>
        public const double MaxStartSeconds = 3600;
        /// <summary>AccountPrefix starts every burst account's name (decision 1: meshburst0001..meshburstN).</summary>
        public const string AccountPrefix = "meshburst";
        /// <summary>CharacterSuffix ends every burst character's name: the account's name followed by it.</summary>
        public const string CharacterSuffix = "Char";
        /// <summary>WindowSeconds is the length of a DB burst window on the burst's own clock.</summary>
        public const double WindowSeconds = 5;

        /// <summary>
        /// Plan is a parsed MESH_DB_BURST: Total arrivals starting StartSeconds after the map server can instantiate
        /// objects, either all at once (Rate 0) or Rate a second for DurationSeconds (Total = Rate times DurationSeconds).
        /// </summary>
        public readonly struct Plan
        {
            /// <summary>Total is the number of arrivals, each a distinct burst character.</summary>
            public readonly int Total;
            /// <summary>Rate is the arrivals per second of a sustained burst, 0 for arrivals at once.</summary>
            public readonly int Rate;
            /// <summary>DurationSeconds is how long a sustained burst lasts, 0 for arrivals at once.</summary>
            public readonly int DurationSeconds;
            /// <summary>StartSeconds is when the first arrival is due, after the map server can instantiate objects.</summary>
            public readonly double StartSeconds;

            /// <summary>Plan holds the values as given; TryParseSetting is the checked way to make one.</summary>
            public Plan(int total, int rate, int durationSeconds, double startSeconds)
            {
                Total = total;
                Rate = rate;
                DurationSeconds = durationSeconds;
                StartSeconds = startSeconds;
            }

            /// <summary>Sustained is true for <c>&lt;R&gt;/s@&lt;t&gt;+&lt;d&gt;</c>, false for N arrivals at once.</summary>
            public bool Sustained => Rate > 0;
        }

        /// <summary>
        /// TryParseSetting reads <c>&lt;N&gt;@&lt;t&gt;</c> (N arrivals at once, t s after the map server can instantiate
        /// objects) or <c>&lt;R&gt;/s@&lt;t&gt;+&lt;d&gt;</c> (R arrivals a second for d s from t). N, R and d are whole
        /// numbers of at least 1, at most MaxArrivals arrivals in all; t is a number from 0 to MaxStartSeconds; spaces
        /// around the parts are ignored. False with an error naming the key otherwise.
        /// </summary>
        public static bool TryParseSetting(string text, out Plan plan, out string error)
        {
            plan = default;
            string value = text == null ? string.Empty : text.Trim();
            int at = value.IndexOf('@');
            if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
            {
                error = $"{EnvironmentKey}={Printable(value)} is not <N>@<t> or <R>/s@<t>+<d>";
                return false;
            }
            string count = value.Substring(0, at).Trim();
            string when = value.Substring(at + 1).Trim();
            bool sustained = count.EndsWith("/s", StringComparison.Ordinal);
            int plus = when.IndexOf('+');
            if (sustained != (plus >= 0) || plus != when.LastIndexOf('+'))
            {
                error = $"{EnvironmentKey}={Printable(value)}: a rate <R>/s goes with a duration +<d> and N arrivals at once with none";
                return false;
            }
            if (sustained)
                count = count.Substring(0, count.Length - 2).Trim();
            string start = sustained ? when.Substring(0, plus).Trim() : when;
            if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > MaxArrivals)
            {
                error = $"{EnvironmentKey}={Printable(value)}: {(sustained ? "R" : "N")} must be a whole number from 1 to {MaxArrivals}";
                return false;
            }
            if (!double.TryParse(start, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double t) || !(t >= 0) || t > MaxStartSeconds)
            {
                error = $"{EnvironmentKey}={Printable(value)}: t must be a number of seconds from 0 to {MaxStartSeconds:0}";
                return false;
            }
            int duration = 0;
            if (sustained)
            {
                string d = when.Substring(plus + 1).Trim();
                if (!int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out duration) || duration < 1 || (long)duration * n > MaxArrivals)
                {
                    error = $"{EnvironmentKey}={Printable(value)}: d must be a whole number of seconds of at least 1, with R times d at most {MaxArrivals} arrivals";
                    return false;
                }
            }
            plan = sustained ? new Plan(n * duration, n, duration, t) : new Plan(n, 0, 0, t);
            error = null;
            return true;
        }

        /// <summary>AccountName is burst account number <paramref name="index"/> (1-based): meshburst0001 for 1.</summary>
        public static string AccountName(int index) => AccountPrefix + Math.Max(0, index).ToString("D4", CultureInfo.InvariantCulture);

        /// <summary>CharacterName is burst character number <paramref name="index"/> (1-based): meshburst0001Char for 1.</summary>
        public static string CharacterName(int index) => AccountName(index) + CharacterSuffix;

        /// <summary>
        /// DueCount is how many arrivals of <paramref name="plan"/> are due <paramref name="elapsed"/> seconds after the
        /// first: every one for arrivals at once, else arrival k (0-based) at k / R s, never more than Total; 0 before 0 s.
        /// </summary>
        public static int DueCount(Plan plan, double elapsed)
        {
            if (plan.Total <= 0 || !(elapsed >= 0))
                return 0;
            if (!plan.Sustained)
                return plan.Total;
            double due = Math.Floor(elapsed * plan.Rate) + 1;
            return due >= plan.Total ? plan.Total : (int)due;
        }

        /// <summary>Milliseconds turns a Stopwatch tick span into whole milliseconds (rounded; 0 for negatives).</summary>
        public static long Milliseconds(long ticks, long frequency)
        {
            if (ticks <= 0 || frequency <= 0)
                return 0;
            return (long)Math.Round(ticks * 1000.0 / frequency, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Percentile is the nearest-rank percentile (share 0 to 1) of the first <paramref name="count"/> values of an
        /// ascending array, or 0 when there are none.
        /// </summary>
        public static int Percentile(int[] ascending, int count, double share)
        {
            if (ascending == null || count <= 0)
                return 0;
            count = Math.Min(count, ascending.Length);
            int rank = (int)Math.Ceiling(share * count) - 1;
            return ascending[Math.Min(count - 1, Math.Max(0, rank))];
        }

        /// <summary>
        /// ReadyLine is an arrival that became ready: <c>DB burst 7: save 3 token 2 get 1 bg 5 ready 11 ms ok</c>, each
        /// step's whole milliseconds and ready the arrival's whole span (i is 1-based).
        /// </summary>
        public static string ReadyLine(int index, long save, long token, long get, long bg, long ready)
        {
            StringBuilder b = new StringBuilder(80);
            b.Append("DB burst ").Append(Count(index)).Append(": save ").Append(Count(save)).Append(" token ").Append(Count(token));
            b.Append(" get ").Append(Count(get)).Append(" bg ").Append(Count(bg)).Append(" ready ").Append(Count(ready)).Append(" ms ok");
            return b.ToString();
        }

        /// <summary>FailedLine is an arrival that failed: <c>DB burst 7: failed token after 12 ms</c> (step save, token, get or bg).</summary>
        public static string FailedLine(int index, string step, long afterMs)
        {
            return "DB burst " + Count(index) + ": failed " + Printable(step) + " after " + Count(afterMs) + " ms";
        }

        /// <summary>
        /// WindowLine is the DB burst window: arrivals started, ready and failed in the window, and the nearest-rank p50,
        /// p95 and max of the ready spans that ended in it, each n/a when none became ready:
        /// <c>DB burst window 5.00 s: started 50 ready 48 failed 0 | ready p50 120 p95 300 max 310 ms</c>.
        /// </summary>
        public static string WindowLine(double seconds, long started, long ready, long failed, int p50, int p95, int max)
        {
            StringBuilder b = new StringBuilder(112);
            b.Append("DB burst window ").Append(Fixed(seconds, "0.00")).Append(" s: started ").Append(Count(started));
            b.Append(" ready ").Append(Count(ready)).Append(" failed ").Append(Count(failed));
            bool any = ready > 0;
            b.Append(" | ready p50 ").Append(any ? Count(p50) : NotAvailable).Append(" p95 ").Append(any ? Count(p95) : NotAvailable);
            b.Append(" max ").Append(any ? Count(max) : NotAvailable).Append(" ms");
            return b.ToString();
        }

        /// <summary>NotAvailable stands for a percentile of a window without a sample.</summary>
        public const string NotAvailable = "n/a";

        /// <summary>
        /// StartLine is logged once, when the first arrival goes: <c>DB burst: 200 arrivals at once at 10 s</c> or
        /// <c>DB burst: 5/s for 40 s from 10 s</c>, the time being when the arrivals actually started, in seconds after the
        /// map server could instantiate objects (later than the setting's t only when the warm-up ran past it).
        /// </summary>
        public static string StartLine(Plan plan, double startedAtSeconds)
        {
            string at = Fixed(startedAtSeconds, "0.##");
            if (plan.Sustained)
                return "DB burst: " + Count(plan.Rate) + "/s for " + Count(plan.DurationSeconds) + " s from " + at + " s";
            return "DB burst: " + Count(plan.Total) + " arrivals at once at " + at + " s";
        }

        /// <summary>DoneLine is logged once, after the last arrival settled: <c>DB burst: done, 198 ready 2 failed</c>.</summary>
        public static string DoneLine(int ready, int failed)
        {
            return "DB burst: done, " + Count(ready) + " ready " + Count(failed) + " failed";
        }

        private static string Printable(string text) => string.IsNullOrEmpty(text) ? "-" : MeshStateSender.CleanForLog(text).Replace(' ', '_');

        private static string Fixed(double value, string format) => (value > 0 && !double.IsInfinity(value) ? value : 0).ToString(format, CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
    }
}
