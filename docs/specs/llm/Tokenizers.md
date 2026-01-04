# Tokenizer Format Specifications

## Overview
Minerva supports three tokenizer sources:
1. GGUF-embedded (bundled with weights)
2. Hugging Face tokenizer.json
3. SentencePiece tokenizer.model

## GGUF-Embedded Tokenizer
Stored in GGUF key-value metadata:
- `tokenizer.ggml.model` - tokenizer type (llama, gpt2, etc.)
- `tokenizer.ggml.tokens` - array of token strings
- `tokenizer.ggml.scores` - array of token scores (for SentencePiece)
- `tokenizer.ggml.merges` - BPE merge rules
- `tokenizer.ggml.bos_token_id` - beginning of sequence
- `tokenizer.ggml.eos_token_id` - end of sequence

## Hugging Face tokenizer.json
JSON format with components:
```json
{
  "version": "1.0",
  "model": {
    "type": "BPE",
    "vocab": { "token": id, ... },
    "merges": ["a b", "ab c", ...]
  },
  "pre_tokenizer": { ... },
  "post_processor": { ... },
  "decoder": { ... }
}
```

## SentencePiece tokenizer.model
Protocol buffer format.
Use SentencePiece library or parse protobuf directly.

Key fields:
- trainer_spec - training parameters
- normalizer_spec - text normalization
- pieces - vocabulary with scores

## Implementation Notes for Minerva
- Prefer GGUF-embedded when available (single file)
- Fall back to tokenizer.json for HF models
- SentencePiece requires protobuf parsing
- All tokenizers produce int[] token IDs
