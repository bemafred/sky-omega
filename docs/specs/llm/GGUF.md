# GGUF File Format Specification

## Source
- Official spec: https://github.com/ggml-org/ggml/blob/master/docs/gguf.md
- Reference implementation: llama.cpp

## Overview
GGUF (GGML Universal Format) is a binary format for storing ML models for inference.
Self-contained: weights, tokenizer, and metadata in a single file.

## File Structure

### Header
| Field | Type | Description |
|-------|------|-------------|
| Magic | uint32 | "GGUF" (0x46554747) |
| Version | uint32 | Format version (currently 3) |
| Tensor Count | uint64 | Number of tensors |
| KV Count | uint64 | Number of key-value metadata pairs |

### Key-Value Metadata
Key-value pairs providing model information.

```
struct gguf_kv {
    gguf_str key;
    gguf_type type;
    gguf_value value;
};
```

### Tensor Info
```
struct gguf_tensor_info {
    gguf_str name;
    uint32_t n_dims;
    uint64_t ne[4];      // dimensions
    ggml_type type;
    uint64_t offset;     // from start of data section
};
```

### Data Section
Raw tensor data, aligned to GGUF_DEFAULT_ALIGNMENT (32 bytes).
Memory-mappable for zero-copy access.

## Data Types
| Type | ID | Description |
|------|-----|-------------|
| F32 | 0 | 32-bit float |
| F16 | 1 | 16-bit float |
| Q4_0 | 2 | 4-bit quantized |
| Q4_1 | 3 | 4-bit quantized (variant) |
| Q5_0 | 6 | 5-bit quantized |
| Q5_1 | 7 | 5-bit quantized (variant) |
| Q8_0 | 8 | 8-bit quantized |
| Q8_1 | 9 | 8-bit quantized (variant) |
| ... | ... | See full spec |

## Implementation Notes for Minerva
- Use MemoryMappedFile for tensor data access
- Parse header eagerly, tensor data lazily
- Validate magic and version before processing
- Respect alignment for tensor offsets
