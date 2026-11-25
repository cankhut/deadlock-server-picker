using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerPickerX.Helpers
{
    // Works out which servers Valve added and took away since the list was last seen, for the
    // message shown when Valve updates its server data. Servers are compared by Valve's code
    // ("fra"), so a server Valve only renames counts as neither added nor removed
    public static class ServerListChanges
    {
        // A longer list is cut short with "and N more", so a big update from Valve cannot
        // stretch the message past the bottom of the screen
        public const int MaxNamesPerList = 10;

        // Names of the servers the current list has and the previous one did not
        public static List<string> Added(IReadOnlyDictionary<string, string> previous, IReadOnlyDictionary<string, string> current)
            => NamesMissingFrom(current, previous);

        // Names of the servers the previous list had and the current one does not
        public static List<string> Removed(IReadOnlyDictionary<string, string> previous, IReadOnlyDictionary<string, string> current)
            => NamesMissingFrom(previous, current);

        // The lines that go below the update message. Null when there is no earlier list to
        // compare with, which is the first time a game opens after this version is installed,
        // and the message then reads exactly as it always has
        public static string? Describe(
            IReadOnlyDictionary<string, string>? previous,
            IReadOnlyDictionary<string, string> current,
            string addedHeader,
            string removedHeader,
            string unchangedLine,
            string moreFormat
            )
        {
            if (previous == null || previous.Count == 0)
            {
                return null;
            }

            List<string> added = Added(previous, current);
            List<string> removed = Removed(previous, current);

            // Valve also updates its data when only addresses change, and saying so is
            // better than saying nothing
            if (added.Count == 0 && removed.Count == 0)
            {
                return unchangedLine;
            }

            List<string> sections = [];

            if (added.Count > 0)
            {
                sections.Add(Section(addedHeader, added, moreFormat));
            }

            if (removed.Count > 0)
            {
                sections.Add(Section(removedHeader, removed, moreFormat));
            }

            return string.Join("\n\n", sections);
        }

        private static List<string> NamesMissingFrom(IReadOnlyDictionary<string, string> list, IReadOnlyDictionary<string, string> other)
            => list
                .Where(server => !other.ContainsKey(server.Key))
                .Select(server => server.Value)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static string Section(string header, List<string> names, string moreFormat)
        {
            List<string> lines = [header];

            lines.AddRange(names.Take(MaxNamesPerList).Select(name => "\u2022 " + name));

            if (names.Count > MaxNamesPerList)
            {
                lines.Add(string.Format(moreFormat, names.Count - MaxNamesPerList));
            }

            return string.Join("\n", lines);
        }
    }
}
