using Frosty.Hash;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Flurry.Editor.DataExplorer
{
    internal enum FlurryFilterCommandType
    {
        Contains,
        StartsWith,
        EndsWith,
        RegEx,
        Type,
        Id,
        Hash
    }

    internal enum FlurryFilterCombineType
    {
        Or,
        And
    }

    internal struct FlurryFilterData
    {
        public string Text;
        public FlurryFilterCommandType Command;
        public FlurryFilterCombineType Combine;
        public bool Not;
    }

    /// <summary>
    /// Parses and evaluates the Data Explorer filter language, e.g.
    /// "sword type:weapon AND NOT contains:placeholder". Stateless except for the
    /// type-descendant cache owned by <see cref="FlurryExplorerState"/>, so instances of
    /// this class may be evaluated on any thread.
    /// </summary>
    internal static class FlurryFilterEngine
    {
        internal const string AndToken = "AND";
        internal const string NotToken = "NOT";

        private static readonly string[] CommandNames = Enum.GetNames(typeof(FlurryFilterCommandType));
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        private sealed class ParsedToken
        {
            public FlurryFilterData Data;
            public List<string> ConsumedWords;
        }

        /// <summary>
        /// Evaluates a filter list against an asset entry. The filter list is expected to be
        /// a snapshot taken on the UI thread so background evaluation cannot race live edits.
        /// </summary>
        internal static bool Matches(IReadOnlyList<FlurryFilterData> filter, string inText, AssetEntry inEntry, FlurryExplorerState state)
        {
            if (filter.Count == 0)
                return true;

            bool result = false;
            for (int i = 0; i < filter.Count; i++)
            {
                FlurryFilterData token = filter[i];
                bool matched = Evaluate(token, inText, inEntry, state);

                if (token.Not)
                    matched = !matched;

                if (i == 0)
                    result = matched;
                else if (token.Combine == FlurryFilterCombineType.And)
                    result &= matched;
                else
                    result |= matched;
            }

            return result;
        }

        /// <summary>
        /// Tokenizes filter text into filter data plus the raw words each token consumed,
        /// so callers can rewrite the text (e.g. to strip plain type tokens). Returns false
        /// when the text cannot be parsed; both output lists are then empty.
        /// </summary>
        internal static bool TryTokenize(string filterText, out List<FlurryFilterData> tokens, out List<List<string>> consumedWords)
        {
            tokens = new List<FlurryFilterData>();
            consumedWords = new List<List<string>>();

            var parsed = new List<ParsedToken>();
            string[] words = filterText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 1 && !words[0].Contains(":"))
            {
                tokens.Add(new FlurryFilterData
                {
                    Text = words[0],
                    Command = FlurryFilterCommandType.Contains,
                    Combine = FlurryFilterCombineType.Or,
                    Not = false
                });
                consumedWords.Add(new List<string> { words[0] });
                return true;
            }

            try
            {
                for (int i = 0; i < words.Length; i++)
                {
                    FlurryFilterCombineType combine = FlurryFilterCombineType.Or;
                    bool not = false;
                    var consumed = new List<string>();

                    if (parsed.Count != 0)
                    {
                        consumed.Add(words[i]);
                        combine = words[i].Equals(AndToken, StringComparison.Ordinal)
                            ? FlurryFilterCombineType.And
                            : FlurryFilterCombineType.Or;
                        i++;
                    }

                    if (words[i].Equals(NotToken, StringComparison.Ordinal))
                    {
                        not = true;
                        consumed.Add(words[i]);
                        i++;
                    }

                    consumed.Add(words[i]);
                    FlurryFilterData data = ParseToken(words, ref i, consumed);
                    data.Combine = combine;
                    data.Not = not;

                    parsed.Add(new ParsedToken { Data = data, ConsumedWords = consumed });
                }
            }
            catch (Exception)
            {
                tokens.Clear();
                consumedWords.Clear();
                return false;
            }

            foreach (ParsedToken token in parsed)
            {
                tokens.Add(token.Data);
                consumedWords.Add(token.ConsumedWords);
            }
            return true;
        }

        private static FlurryFilterData ParseToken(string[] words, ref int index, List<string> consumed)
        {
            string word = words[index];
            string[] parts = word.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                TryMatchCommandPrefix(parts[0], out FlurryFilterCommandType command);
                return new FlurryFilterData { Text = parts[1], Command = command };
            }

            if (TryMatchExactCommand(word, out FlurryFilterCommandType exact) && index + 1 < words.Length)
            {
                index++;
                consumed.Add(words[index]);
                return new FlurryFilterData { Text = words[index], Command = exact };
            }

            return new FlurryFilterData { Text = word, Command = FlurryFilterCommandType.Contains };
        }

        private static bool Evaluate(FlurryFilterData token, string inText, AssetEntry inEntry, FlurryExplorerState state)
        {
            switch (token.Command)
            {
                case FlurryFilterCommandType.Contains:
                    return inText.IndexOf(token.Text, StringComparison.OrdinalIgnoreCase) >= 0;

                case FlurryFilterCommandType.StartsWith:
                    return inText.StartsWith(token.Text, StringComparison.OrdinalIgnoreCase);

                case FlurryFilterCommandType.EndsWith:
                    return inText.EndsWith(token.Text, StringComparison.OrdinalIgnoreCase);

                case FlurryFilterCommandType.RegEx:
                    return Regex.IsMatch(inText, token.Text, RegexOptions.None, RegexTimeout);

                case FlurryFilterCommandType.Type:
                    return FlurryExplorerEngine.GetTypeDescendants(state, token.Text).Contains(inEntry.Type ?? "");

                case FlurryFilterCommandType.Id:
                    return MatchesId(token.Text, inEntry);

                case FlurryFilterCommandType.Hash:
                    return MatchesHash(token.Text, inEntry);

                default:
                    return false;
            }
        }

        private static bool MatchesId(string text, AssetEntry entry)
        {
            if (entry is EbxAssetEntry ebx)
                return Guid.TryParse(text, out Guid guid) && ebx.Guid.Equals(guid);

            return entry is ResAssetEntry res
                && ulong.TryParse(text, NumberStyles.HexNumber, null, out ulong resRid)
                && res.ResRid == resRid;
        }

        private static bool MatchesHash(string text, AssetEntry entry)
        {
            if (!(entry is EbxAssetEntry || entry is ResAssetEntry))
                return false;

            return int.TryParse(text, NumberStyles.HexNumber, null, out int hash)
                && Fnv1.HashString(entry.Name.ToLower()) == hash;
        }

        private static bool TryMatchCommandPrefix(string candidate, out FlurryFilterCommandType command)
        {
            string lower = candidate.ToLowerInvariant();
            foreach (string name in CommandNames)
            {
                if (lower.StartsWith(name.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    command = (FlurryFilterCommandType)Enum.Parse(typeof(FlurryFilterCommandType), name);
                    return true;
                }
            }

            command = FlurryFilterCommandType.Contains;
            return false;
        }

        private static bool TryMatchExactCommand(string candidate, out FlurryFilterCommandType command)
        {
            foreach (string name in CommandNames)
            {
                if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    command = (FlurryFilterCommandType)Enum.Parse(typeof(FlurryFilterCommandType), name);
                    return true;
                }
            }

            command = FlurryFilterCommandType.Contains;
            return false;
        }
    }
}