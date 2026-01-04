# ADR-001: Minerva Weight and Tokenizer Format Support

## Status
Proposed

## Context
Minerva is a zero-GC, .NET-native tensor inference substrate. To load pre-trained models, Minerva must parse industry-standard weight and tokenizer formats. We follow the same philosophy as Mercury: 100% spec compliance, memory-mapped where possible, zero external dependencies beyond BCL.

## Decision

### Weight Formats (Priority Order)

1. **GGUF** - Primary format
   - Spec: docs/specs/llm/GGUF.md
   - Self-contained (weights + tokenizer + metadata)
   - Memory-mappable tensor data
   - Quantization support (Q4, Q8, etc.)

2. **SafeTensors** - Secondary format
   - Spec: docs/specs/llm/SafeTensors.md
   - 8-byte header size + JSON header + contiguous tensor data
   - Little-endian, row-major
   - Memory-mappable

### Tokenizer Formats

1. **GGUF-embedded** - Bundled with GGUF weights
2. **tokenizer.json** - Hugging Face format (JSON)
3. **tokenizer.model** - SentencePiece (protobuf)

### Implementation Approach

- Memory-mapped file access via `System.IO.MemoryMappedFiles`
- Zero-copy tensor views using `Span<T>` / `Memory<T>`
- No GC allocations in hot paths
- BCL-only dependencies

## Consequences

- Full ecosystem compatibility (Ollama, LM Studio, HuggingFace models)
- Sovereign infrastructure - no llama.cpp, no GGML, no Python
- Same architectural patterns as Mercury

## Implementation Tasks

- [ ] GgufReader: Header parsing
- [ ] GgufReader: KV metadata parsing
- [ ] GgufReader: Tensor info parsing
- [ ] GgufReader: Memory-mapped tensor access
- [ ] SafeTensorsReader: Header parsing
- [ ] SafeTensorsReader: JSON metadata parsing
- [ ] SafeTensorsReader: Memory-mapped tensor access
- [ ] Tokenizer: GGUF-embedded extraction
- [ ] Tokenizer: tokenizer.json parsing
- [ ] Tokenizer: BPE encode/decode
