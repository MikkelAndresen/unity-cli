using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace UnityCliConnector
{
    /// <summary>
    /// A snippet this domain has already compiled, kept so repeating the same exec skips csc.
    /// </summary>
    internal sealed class CompiledSnippet
    {
        internal readonly Assembly Assembly;
        internal readonly MethodInfo Method;

        internal CompiledSnippet(Assembly assembly, MethodInfo method)
        {
            Assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
            Method = method ?? throw new ArgumentNullException(nameof(method));
        }
    }

    /// <summary>
    /// Compiled-snippet lookup for exec. The key covers everything that decides what csc
    /// produces: the generated source (its usings and Execute signature included), the
    /// reference set handed to the compiler, the compiler override, and the identity of the
    /// domain that loads the bytes. Nothing else has to invalidate an entry — a script
    /// recompile reloads the domain, which drops this table along with the assemblies the
    /// cached IL binds to.
    ///
    /// Only reached from the editor main thread (exec runs on HttpServer's command queue),
    /// so the table is unsynchronized by design.
    /// </summary>
    internal static class ExecCompileCache
    {
        /// Bound on lookup entries only. Mono cannot unload an assembly, so an evicted
        /// snippet stays in the domain — still counted by AppDomain.GetAssemblies, still
        /// holding its types alive — until the next domain reload. The bound caps the table
        /// and its key strings, not the assemblies.
        internal const int MaxEntries = 256;

        static readonly Dictionary<string, CompiledSnippet> s_Entries = new();
        static readonly Queue<string> s_EvictionOrder = new();

        // A snippet's IL binds to the assembly copies of the domain that compiled it, so an
        // entry must never be handed to a different domain even if its key matches there.
        static readonly string s_DomainId = Guid.NewGuid().ToString("N");

        internal static int Count => s_Entries.Count;

        internal static string KeyFor(string source, string cscPath, string dotnetPath,
            IReadOnlyList<string> references)
        {
            // ThrowIfNull is .NET 6 and this assembly compiles against Unity's
            // .NET Standard 2.1 reference set, so the long form is what compiles.
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (references == null) throw new ArgumentNullException(nameof(references));

            var sb = new StringBuilder();
            sb.Append(s_DomainId).Append('\n');
            sb.Append(cscPath ?? string.Empty).Append('\n');
            sb.Append(dotnetPath ?? string.Empty).Append('\n');

            // Sorted set, not sequence: the domain's assembly load order is not part of
            // what csc compiles against, so it must not decide the key.
            var sorted = new List<string>(references);
            sorted.Sort(StringComparer.Ordinal);
            for (var i = 0; i < sorted.Count; i++)
                sb.Append(sorted[i]).Append('\n');
            sb.Append(source);

            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())))
                .Replace("-", string.Empty);
        }

        internal static bool TryGet(string key, out CompiledSnippet snippet)
        {
            return s_Entries.TryGetValue(key, out snippet);
        }

        internal static void Put(string key, CompiledSnippet snippet)
        {
            if (s_Entries.TryGetValue(key, out var existing))
            {
                // Rewriting an existing key must not add a second eviction slot.
                if (!ReferenceEquals(existing, snippet)) s_Entries[key] = snippet;
                return;
            }

            s_Entries[key] = snippet;
            s_EvictionOrder.Enqueue(key);

            while (s_Entries.Count > MaxEntries && s_EvictionOrder.TryDequeue(out var oldest))
                s_Entries.Remove(oldest);
        }

        internal static void ClearForTests()
        {
            s_Entries.Clear();
            s_EvictionOrder.Clear();
        }
    }
}
