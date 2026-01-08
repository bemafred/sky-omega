namespace SkyOmega.Minerva.Core.Tokenizers;

/// <summary>
/// Tokenizer abstraction supporting BPE, SentencePiece, and GGUF-embedded tokenizers.
/// See docs/specs/llm/Tokenizers.md for format specifications.
/// </summary>
public abstract class Tokenizer
{
    public abstract ReadOnlySpan<int> Encode(ReadOnlySpan<char> text);
    public abstract string Decode(ReadOnlySpan<int> tokens);
}
