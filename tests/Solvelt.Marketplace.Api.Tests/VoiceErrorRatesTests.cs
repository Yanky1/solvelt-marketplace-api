using Solvelt.Marketplace.Api.Voice.TestSet;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Word and character error rates for the recognition test set (decision V-46).</summary>
public sealed class VoiceErrorRatesTests
{
    [Fact]
    public void Identical_words_in_any_style_have_no_errors()
    {
        Assert.Equal(0, ErrorRates.Wer("Do you have the Pewter Kiddush Cup?", "do you have the pewter kiddush cup").Errors);
        Assert.Equal(0, ErrorRates.Wer("My code is 4 7 2.", "my code is four seven two").Errors);                 // a digit written as a word is the same digit
        Assert.Equal(0, ErrorRates.Wer("oh five", "0 5").Errors);
        Assert.Equal(0, ErrorRates.Cer("Hello, world!", "hello world").Errors);
    }

    [Fact]
    public void Substitutions_deletions_and_insertions_are_counted_apart()
    {
        var sub = ErrorRates.Wer("a b c d", "a x c d"); Assert.Equal((1, 0, 0, 4), (sub.Substitutions, sub.Deletions, sub.Insertions, sub.ReferenceLength));
        var del = ErrorRates.Wer("a b c d", "a c d"); Assert.Equal((0, 1, 0), (del.Substitutions, del.Deletions, del.Insertions));
        var ins = ErrorRates.Wer("a b c d", "a b x c d"); Assert.Equal((0, 0, 1), (ins.Substitutions, ins.Deletions, ins.Insertions));
        Assert.Equal(0.25, sub.Rate);
    }

    [Fact]
    public void Nothing_heard_is_every_word_deleted_and_nothing_said_is_every_word_inserted()
    {
        var none = ErrorRates.Wer("one two three", "");
        Assert.Equal((0, 3, 0, 3), (none.Substitutions, none.Deletions, none.Insertions, none.ReferenceLength));
        Assert.Equal(1.0, none.Rate);
        Assert.Equal(0, ErrorRates.Wer("", "").Rate);
        Assert.Equal(1, ErrorRates.Wer("", "extra").Errors);
        Assert.True(ErrorRates.Wer("a", "a b c d").Rate > 1);                                                     // more extra words than reference words
    }

    [Fact]
    public void Character_errors_count_a_missing_space_and_a_wrong_letter()
    {
        Assert.Equal(1, ErrorRates.Cer("kiddush cup", "kiddush cap").Errors);
        Assert.Equal(1, ErrorRates.Cer("kiddush cup", "kiddushcup").Errors);
        Assert.Equal("kiddush cup".Length, ErrorRates.Cer("kiddush cup", "kiddush cup").ReferenceLength);
    }

    [Theory]
    [InlineData("שָׁלוֹם עֲלֵיכֶם", "שלום עליכם")]            // Hebrew points are not words
    [InlineData("װאס מאכסטו", "וואס מאכסטו")]               // the Yiddish digraph letter is the pair it stands for
    [InlineData("צה״ל", "צהל")]                              // gershayim
    [InlineData("שלום", "שלוּם")]
    [InlineData("מלך", "מלכ")]                               // a final form is the ordinary letter
    public void Hebrew_script_is_compared_by_its_letters(string reference, string hypothesis)
    {
        Assert.Equal(0, ErrorRates.Wer(reference, hypothesis).Errors);
        Assert.Equal(0, ErrorRates.Cer(reference, hypothesis).Errors);
    }

    [Fact]
    public void A_different_yiddish_word_is_still_an_error()
    {
        Assert.Equal(1, ErrorRates.Wer("איך וויל קויפן", "איך וויל פארקויפן").Errors);
        Assert.Equal(1, ErrorRates.Wer("two cups", "to cups").Errors);                                            // "to" is not "two": the recogniser got it wrong
    }

    [Fact]
    public void A_corpus_rate_weights_a_long_phrase_more_than_a_short_one()
    {
        var total = ErrorRates.Wer("a", "b") + ErrorRates.Wer("a b c d e f g h i j", "a b c d e f g h i j");   // 1 error in 11 words, not the mean of 100% and 0%
        Assert.Equal((1, 11), (total.Errors, total.ReferenceLength));
        Assert.True(Math.Abs(total.Rate - 1.0 / 11) < 1e-9);
    }
}
