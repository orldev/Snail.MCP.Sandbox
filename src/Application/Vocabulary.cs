using System.Text;

namespace Snail.MCP.Sandbox.Application;

/// <summary>Words, and how strongly one of them being in a name or a description answers a question.</summary>
/// <remarks>
/// The same scoring as the other servers of this fleet, so that a question phrased one way finds the tool
/// in every one of them. The weights say what they mean: a word in the name of a tool, or one of the words
/// the index knows it by, is what the tool is; a word in its description is only something the tool mentions.
/// The words everyone's question is made of — what, the, in, my — are dropped, because a description long
/// enough to contain all of them would otherwise answer every question ever asked.
/// </remarks>
public static class Vocabulary
{
    private const double InTheName = 4.0;

    private const double LikeTheName = 2.5;

    private const double InTheDescription = 1.0;

    /// <summary>The letters two words must agree on to be forms of each other; below it every word is a form of something.</summary>
    private const int ShortestStem = 4;

    /// <summary>The words a question is made of rather than about; they say nothing about which tool it wants.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "a", "all", "an", "and", "any", "are", "as", "at", "be", "but", "by", "can", "do", "for", "from", "has", "have",
        "how", "i", "if", "in", "is", "it", "its", "me", "my", "no", "not", "of", "on", "one", "or", "out", "so", "that",
        "the", "their", "them", "then", "there", "they", "this", "to", "up", "was", "what", "when", "where", "which",
        "who", "why", "will", "with", "would", "you", "your"
    };

    /// <summary>The words of a text, lowercased, with everything that is not a letter or a digit as a boundary.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        var words = new List<string>();
        var word = new StringBuilder();

        foreach (var letter in text ?? string.Empty)
        {
            if (char.IsLetterOrDigit(letter))
            {
                word.Append(char.ToLowerInvariant(letter));
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
            words.Add(word.ToString());

        return words;
    }

    /// <summary>How well a name and a description answer a question, as the sum of what each of its words finds.</summary>
    public static double Score(string question, string name, string? description) =>
        Score(question, name, null, description);

    /// <summary>The same, for a tool the index knows other words for than the ones it is named with.</summary>
    public static double Score(string question, string name, string? keywords, string? description)
    {
        var named = Words(name);
        var meant = Words(keywords);
        var described = Words(description);

        return Words(question)
            .Where(asked => !Common.Contains(asked))
            .Sum(asked => Weight(asked, named, meant, described));
    }

    private static double Weight(string asked, IReadOnlyList<string> named, IReadOnlyList<string> meant, IReadOnlyList<string> described)
    {
        if (named.Contains(asked, StringComparer.Ordinal) || meant.Contains(asked, StringComparer.Ordinal))
            return InTheName;

        if (Forms(named, asked) || Forms(meant, asked))
            return LikeTheName;

        return described.Contains(asked, StringComparer.Ordinal) || Forms(described, asked) ? InTheDescription : 0.0;
    }

    private static bool Forms(IReadOnlyList<string> words, string asked) =>
        words.Any(word => Shares(word, asked));

    /// <summary>
    /// Whether two words are forms of each other: they agree on their first four letters, or entirely
    /// when one of them is shorter than that. "plot" and "plotting" part on the fifth letter, which is
    /// why this is not simply one word starting with the other.
    /// </summary>
    private static bool Shares(string word, string asked)
    {
        var shortest = Math.Min(word.Length, asked.Length);
        var enough = Math.Min(ShortestStem, shortest);
        var common = 0;

        while (common < shortest && word[common] == asked[common])
            common++;

        return common >= enough;
    }
}
