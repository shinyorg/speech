using System.Text;

namespace Shiny.AiConversation.Voice.Infrastructure;

/// <summary>
/// Decides whether a spoken utterance is, in its entirety, an answer to one of the AI's pending questions:
/// a choice label ("large"), an ordinal ("the second one", "option two"), a yes/no synonym for a question
/// with Yes/No choices ("yeah", "nope"), or several labels when the question allows more than one.
/// Whole-utterance only, so the AI's own voice reading its options ("small, medium or large?") doesn't match.
/// </summary>
static class AnswerMatcher
{
    static readonly string[] LeadIns =
    [
        "um ", "uh ", "er ", "hmm ", "ok ", "okay ", "well ",
        "i'll take ", "ill take ", "i'll have ", "ill have ", "i'd like ", "id like ", "i want ", "let's go with ",
        "lets go with ", "go with ", "make it ", "i choose ", "i pick ", "my answer is ", "answer "
    ];

    static readonly string[] TailOuts = [" please", " thanks", " thank you"];

    static readonly HashSet<string> Yes = ["yes", "yeah", "yep", "yup", "ya", "sure", "ok", "okay", "yes please", "please do", "correct", "right", "absolutely", "of course", "sounds good", "do it", "affirmative"];
    static readonly HashSet<string> No = ["no", "nope", "nah", "no thanks", "no thank you", "not really", "negative", "don't", "dont"];

    static readonly Dictionary<string, int> Ordinals = new()
    {
        ["first"] = 1, ["1st"] = 1, ["one"] = 1, ["1"] = 1, ["a"] = 1,
        ["second"] = 2, ["2nd"] = 2, ["two"] = 2, ["2"] = 2, ["b"] = 2,
        ["third"] = 3, ["3rd"] = 3, ["three"] = 3, ["3"] = 3, ["c"] = 3,
        ["fourth"] = 4, ["4th"] = 4, ["four"] = 4, ["4"] = 4, ["d"] = 4,
        ["fifth"] = 5, ["5th"] = 5, ["five"] = 5, ["5"] = 5, ["e"] = 5,
        ["last"] = -1
    };


    /// <summary>
    /// The question answered and the chosen choice ids, or null when the utterance isn't (only) an answer.
    /// Questions without choices never match: any speech could be their answer.
    /// </summary>
    public static (AiQuestion Question, IReadOnlyList<string> ChoiceIds)? Match(string utterance, IReadOnlyList<AiQuestion> questions)
    {
        var text = Strip(Normalize(utterance));
        if (text.Length == 0)
            return null;

        foreach (var question in questions)
        {
            if (question.Choices is not { Count: > 0 } choices)
                continue;

            if (MatchOne(text, choices) is { } single)
                return (question, [single]);

            if (question.AllowMultiple && MatchMany(text, choices) is { } many)
                return (question, many);
        }
        return null;
    }


    static string? MatchOne(string text, IReadOnlyList<AiChoice> choices)
    {
        foreach (var choice in choices)
        {
            var label = Normalize(choice.Label);
            if (text == label || text == "the " + label || text == Strip(label))
                return choice.Id;
        }

        // yes/no synonyms, when the question has Yes / No choices
        var yesNo = Yes.Contains(text) ? "yes" : No.Contains(text) ? "no" : null;
        if (yesNo != null)
        {
            var choice = choices.FirstOrDefault(c => Normalize(c.Label) == yesNo || Normalize(c.Id) == yesNo || Normalize(c.Id) == yesNo[..1]);
            if (choice != null)
                return choice.Id;
        }

        // "the second one", "option two", "number 2", "b", "the last one"
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        words.RemoveAll(w => w is "the" or "option" or "number" or "choice");
        if (words.Count == 2 && words[1] == "one" && Ordinals.ContainsKey(words[0]))
            words.RemoveAt(1);   // "the second one"

        if (words.Count == 1 && Ordinals.TryGetValue(words[0], out var position))
        {
            var index = position == -1 ? choices.Count - 1 : position - 1;
            if (index >= 0 && index < choices.Count)
                return choices[index].Id;
        }
        return null;
    }


    static List<string>? MatchMany(string text, IReadOnlyList<AiChoice> choices)
    {
        var pieces = text.Replace(" and ", ",").Replace(" & ", ",").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length < 2)
            return null;

        var ids = new List<string>();
        foreach (var piece in pieces)
        {
            if (MatchOne(Strip(piece), choices) is not { } id)
                return null;

            if (!ids.Contains(id))
                ids.Add(id);
        }
        return ids;
    }


    /// <summary>
    /// Lower case, punctuation removed (apostrophes kept), whitespace collapsed.
    /// </summary>
    static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value.ToLowerInvariant())
        {
            if (Char.IsLetterOrDigit(c) || c == '\'' || c == '&')
            {
                sb.Append(c);
                space = false;
            }
            else if (!space && sb.Length > 0)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().Trim();
    }


    /// <summary>
    /// Drops conversational lead-ins ("I'll take…") and tails ("…please") around the answer.
    /// </summary>
    static string Strip(string text)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var lead in LeadIns)
            {
                if (text.StartsWith(lead, StringComparison.Ordinal) && text.Length > lead.Length)
                {
                    text = text[lead.Length..];
                    changed = true;
                }
            }
            foreach (var tail in TailOuts)
            {
                if (text.EndsWith(tail, StringComparison.Ordinal) && text.Length > tail.Length && !Yes.Contains(text) && !No.Contains(text))
                {
                    text = text[..^tail.Length];
                    changed = true;
                }
            }
        }
        return text.Trim();
    }
}
