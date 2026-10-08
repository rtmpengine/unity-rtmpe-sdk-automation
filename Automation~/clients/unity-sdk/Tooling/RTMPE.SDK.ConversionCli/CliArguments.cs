using System;
using System.IO;

namespace RTMPE.SDK.ConversionCli
{
    /// <summary>
    /// The argument-reading rules every verb in this host shares.
    /// <para>
    /// Each verb parses its own options, so a rule written into one of them is a
    /// rule the other five do not have. The option-shaped-value check was written
    /// into one; its absence from the rest is what let <c>--out --source</c> write
    /// a report into a directory named <c>--source</c> and exit 0, and an empty
    /// <c>--file</c> abort five verbs where 1 is documented. Reading a value
    /// through here is what makes the rules uniform, and the shared exit-code
    /// contract — 1 for a usage fault, 2 for the environment — meaningful across
    /// the host rather than per-verb.
    /// </para>
    /// </summary>
    internal static class CliArguments
    {
        /// <summary>
        /// True when the token after <paramref name="index"/> is usable as that
        /// option's value. A missing token, the next option, and an empty or
        /// blank string are each a usage fault; a <c>switch</c> guarded on this
        /// falls through to its usage branch, so no verb has to spell the
        /// refusal itself.
        /// </summary>
        internal static bool HasValue(string[] args, int index)
        {
            if (args is null || index + 1 >= args.Length)
            {
                return false;
            }

            string value = args[index + 1];
            return !string.IsNullOrWhiteSpace(value)
                && !value.StartsWith("--", StringComparison.Ordinal);
        }

        /// <summary>
        /// Resolves <paramref name="value"/> to an absolute path, reporting the
        /// verbs' documented usage exit rather than letting the framework's own
        /// exception end the process. A caller that reaches the file system with
        /// an unresolvable path aborts at 134, where the operator — and the
        /// wizard that renders "Engine exit N" — is told to expect 1.
        /// </summary>
        internal static bool TryResolveFullPath(
            string value, string option, TextWriter stderr, out string resolved)
        {
            try
            {
                resolved = Path.GetFullPath(value);
                return true;
            }
            catch (Exception e) when (e is ArgumentException
                || e is PathTooLongException
                || e is NotSupportedException)
            {
                stderr.WriteLine("error: " + option + " is not a usable path: " + e.Message);
                resolved = null;
                return false;
            }
        }

        /// <summary>
        /// Reads a file the operator named, answering a permission, a lock, or a
        /// file removed since it was seen with the verbs' environment exit rather
        /// than an unhandled exception. Existence was checked is not the same as
        /// readable, and the two are separated by a window every real run has.
        /// </summary>
        internal static bool TryReadAllBytes(
            string path, string label, TextWriter stderr, out byte[] bytes)
        {
            try
            {
                bytes = File.ReadAllBytes(path);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                stderr.WriteLine("error: cannot read " + label + ": " + e.Message);
                bytes = null;
                return false;
            }
        }
    }
}
