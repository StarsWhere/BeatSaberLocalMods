using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ModUiChinese
{
    public sealed class TranslationEngine
    {
        sealed class Template
        {
            public Regex Pattern;
            public string Translation;
            public int ArgumentCount;
            public int LiteralLength;
        }
        sealed class Table
        {
            public readonly Dictionary<string, string> Exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Template> Templates = new List<Template>();
            public readonly List<KeyValuePair<string, string>> Prefixes = new List<KeyValuePair<string, string>>();
            public readonly List<KeyValuePair<string, string>> Suffixes = new List<KeyValuePair<string, string>>();
        }
        readonly Dictionary<string, Table> tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        readonly Table fallback = new Table();
        readonly HashSet<string> disabled;
        readonly bool enabled;
        static readonly Regex Placeholder = new Regex(@"\{([0-9]+)(?:,[^}:]+)?(?::[^}]+)?\}", RegexOptions.CultureInvariant);
        static readonly Regex Tags = new Regex(@"(<[^>]+>)", RegexOptions.CultureInvariant);
        static readonly Regex EnglishDate = new Regex(@"(?:(\d{1,2})\.\s*)?\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+(\d{4}|\d{2})(?!\d)", RegexOptions.CultureInvariant);
        static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        // Plugin ids can differ from the assembly names used as UI scopes.
        // Keep these as separate values rather than assuming they are aliases.
        internal static string[] InstalledScopes(IEnumerable<KeyValuePair<string, string>> plugins, IEnumerable<string> knownScopes)
        {
            var known = new HashSet<string>(knownScopes, StringComparer.Ordinal);
            return plugins.Select(plugin => plugin.Value)
                .Where(scope => !String.IsNullOrWhiteSpace(scope) && known.Contains(scope))
                .Concat(new[] { "Common", "GameFeedback" })
                .Distinct(StringComparer.Ordinal).OrderBy(scope => scope, StringComparer.Ordinal).ToArray();
        }


        public TranslationEngine(IDictionary<string, Dictionary<string, string>> catalog) : this(catalog, null) { }
        public TranslationEngine(IDictionary<string, Dictionary<string, string>> catalog, IEnumerable<string> disabledScopes) : this(catalog, disabledScopes, true) { }
        public TranslationEngine(IDictionary<string, Dictionary<string, string>> catalog, IEnumerable<string> disabledScopes, bool enabled)
        {
            this.enabled = enabled;
            disabled = new HashSet<string>(disabledScopes ?? new string[0], StringComparer.Ordinal);
            foreach (var scope in catalog.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var table = new Table();
                foreach (var entry in scope.Value.OrderBy(x => x.Key, StringComparer.Ordinal)) Add(table, entry.Key, entry.Value);
                tables.Add(scope.Key, table);
            }
            Table common;
            if (!disabled.Contains("Common") && tables.TryGetValue("Common", out common))
                foreach (var entry in common.Exact) Add(fallback, entry.Key, entry.Value);
            foreach (var table in tables.Values.Concat(new[] { fallback }))
                table.Templates.Sort((left, right) => right.LiteralLength.CompareTo(left.LiteralLength));
        }
        static void Add(Table table, string source, string target)
        {
            if (String.IsNullOrEmpty(source) || String.IsNullOrWhiteSpace(target)) throw new ArgumentException("Empty UI translation");
            ValidateTemplate(source, target);
            if (table.Exact.ContainsKey(source)) return;
            table.Exact.Add(source, target);
            var matches = Placeholder.Matches(source);
            if (matches.Count != 0)
            {
                var pattern = new StringBuilder("^", source.Length * 2);
                var used = new HashSet<int>();
                int start = 0, count = 0;
                foreach (Match match in matches)
                {
                    pattern.Append(Regex.Escape(source.Substring(start, match.Index - start)));
                    int index = Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    pattern.Append(used.Add(index) ? "(?<p" + index + ">.*?)" : "\\k<p" + index + ">");
                    count = Math.Max(count, index + 1);
                    start = match.Index + match.Length;
                }
                pattern.Append(Regex.Escape(source.Substring(start))).Append("$");
                table.Templates.Add(new Template { Pattern = new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline), Translation = target, ArgumentCount = count,
                    LiteralLength = source.Length - matches.Cast<Match>().Sum(value => value.Length) });
            }
            else if (source.Length >= 4 && Char.IsWhiteSpace(source[source.Length - 1]) && source.Trim().Length >= 3)
                table.Prefixes.Add(new KeyValuePair<string, string>(source, target));
            else if (source.Length >= 4 && Char.IsWhiteSpace(source[0]) && source.Trim().Length >= 3)
                table.Suffixes.Add(new KeyValuePair<string, string>(source, target));
        }
        internal static void ValidateTemplate(string source, string target)
        {
            var matches = Placeholder.Matches(source);
            if (matches.Count == 0) return;
            int count = 0;
            foreach (Match match in matches)
            {
                int index;
                if (!Int32.TryParse(match.Groups[1].Value, out index) || index >= 256)
                    throw new FormatException("Unsupported translation placeholder index");
                count = Math.Max(count, index + 1);
            }
            var arguments = Enumerable.Repeat<object>("", count).ToArray();
            String.Format(CultureInfo.InvariantCulture, source, arguments);
            String.Format(CultureInfo.InvariantCulture, target, arguments);
        }
        static bool TryTable(Table table, string text, out string result)
        {
            if (table.Exact.TryGetValue(text, out result)) return true;
            string trimmed = text.Trim();
            if (trimmed != text && table.Exact.TryGetValue(trimmed, out result))
            {
                int start = text.IndexOf(trimmed, StringComparison.Ordinal);
                result = text.Substring(0, start) + result + text.Substring(start + trimmed.Length);
                return true;
            }
            foreach (var template in table.Templates)
            {
                var match = template.Pattern.Match(text);
                if (!match.Success) continue;
                var args = new object[template.ArgumentCount];
                for (int i = 0; i < args.Length; i++) args[i] = match.Groups["p" + i].Value;
                result = String.Format(CultureInfo.InvariantCulture, template.Translation, args);
                return true;
            }
            foreach (var prefix in table.Prefixes.OrderByDescending(x => x.Key.Length))
                if (text.StartsWith(prefix.Key, StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = text.Substring(prefix.Key.Length), translated;
                    result = prefix.Value + (TryTable(table, remainder, out translated) ? translated : remainder);
                    return true;
                }
            foreach (var suffix in table.Suffixes.OrderByDescending(x => x.Key.Length))
                if (text.EndsWith(suffix.Key, StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = text.Substring(0, text.Length - suffix.Key.Length), translated;
                    result = (TryTable(table, remainder, out translated) ? translated : remainder) + suffix.Value;
                    return true;
                }
            result = text;
            return false;
        }
        public string Translate(string text, string scope)
        {
            if (!enabled || String.IsNullOrEmpty(text)) return text;
            if (scope != null && disabled.Contains(scope)) return text;
            // Energy rows are independently selected and ordered. Translating
            // them separately prevents a placeholder from consuming later rows.
            if (scope == "ActivityEnergyCounter" && text.IndexOf('\n') >= 0)
                return String.Join("\n", text.Split('\n').Select(line => Translate(line, scope)).ToArray());
            if (scope == "ActivityEnergyCounter" && text.IndexOf("   |   ", StringComparison.Ordinal) >= 0)
                return String.Join("   |   ", text.Split(new[] { "   |   " }, StringSplitOptions.None)
                    .Select(item => Translate(item, scope)).ToArray());
            if (scope == "SiraUtil" && text.IndexOf('\n') >= 0)
            {
                var lines = text.Split('\n');
                if (Tags.Replace(lines[0], "").Trim() == "Score Submission Disabled By")
                {
                    lines[0] = Translate(lines[0], scope);
                    string reasonScope = null;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string owner = lines[i].Trim();
                        if (tables.ContainsKey(owner))
                        {
                            reasonScope = owner;
                            lines[i] = Translate(lines[i], owner);
                        }
                        else if (reasonScope != null && lines[i].StartsWith("<size=", StringComparison.Ordinal))
                            lines[i] = Translate(lines[i], reasonScope);
                        else if (!String.IsNullOrWhiteSpace(lines[i])) reasonScope = null;
                    }
                    return String.Join("\n", lines);
                }
            }
            if (scope == "BetterSongSearch")
                text = EnglishDate.Replace(text, m => {
                    int year = Int32.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    if (year < 100) year += 2000;
                    string day = m.Groups[1].Success ? Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + "日" : "";
                    return year + "年" + (Array.IndexOf(Months, m.Groups[2].Value) + 1) + "月" + day;
                });
            string result;
            Table table;
            if (!text.Any(x => (x >= 'A' && x <= 'Z') || (x >= 'a' && x <= 'z')))
            {
                if (scope != null && tables.TryGetValue(scope, out table) && table.Exact.TryGetValue(text, out result)) return result;
                return fallback.Exact.TryGetValue(text, out result) ? result : text;
            }
            if (scope != null && tables.TryGetValue(scope, out table) && TryTable(table, text, out result)) return result;
            if (TryTable(fallback, text, out result)) return result;
            if (text.IndexOf('<') < 0) return text;
            var pieces = Tags.Split(text);
            if (pieces.Length == 1) return text;
            bool changed = false;
            for (int i = 0; i < pieces.Length; i += 2)
            {
                if (pieces[i].Length == 0) continue;
                string translated = Translate(pieces[i], scope);
                if (translated != pieces[i]) { pieces[i] = translated; changed = true; }
            }
            return changed ? String.Concat(pieces) : text;
        }
        public bool HasScope(string scope) { return scope != null && tables.ContainsKey(scope); }
        public bool IsScopeEnabled(string scope) { return enabled && (scope == null || !disabled.Contains(scope)); }
    }
}
